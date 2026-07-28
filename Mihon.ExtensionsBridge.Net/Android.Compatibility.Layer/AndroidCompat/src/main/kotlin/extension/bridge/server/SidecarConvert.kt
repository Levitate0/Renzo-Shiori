package extension.bridge.server

import com.googlecode.d2j.dex.Dex2jar
import com.googlecode.d2j.reader.MultiDexFileReader
import com.googlecode.dex2jar.tools.BaksmaliBaseDexExceptionHandler
import org.objectweb.asm.ClassReader
import org.objectweb.asm.ClassVisitor
import org.objectweb.asm.ClassWriter
import org.objectweb.asm.FieldVisitor
import org.objectweb.asm.MethodVisitor
import org.objectweb.asm.Opcodes
import java.io.File
import java.util.zip.ZipEntry
import java.util.zip.ZipFile
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream

/**
 * DEX -> JAR conversion for the JVM sidecar. Port of the .NET Dex2JarConverter, plus a final pass
 * that downgrades classes to Java 6 and strips StackMapTable so a real JVM verifies them via the
 * older inference verifier — sidestepping the malformed frames newer R8/dex2jar output produces
 * (which the split verifier rejects with "Expecting a stackmap frame..."). Runs on the JVM (no IKVM).
 */
object SidecarConvert {
    private const val REPLACEMENT_PATH = "xyz/nulldev/androidcompat/replace"
    private val classesToReplace = setOf("java/text/SimpleDateFormat")
    private val removeNamespaces = setOf(
        "org.apache.commons.lang3",
        "org.apache.commons.text",
        "org.brotli.dec",
    )

    /**
     * Full pipeline. Preferred path (handles the newest keiyoushi builds that dex2jar mistranslates):
     *   dex2jar  -> sigJar  (correct generic Signatures, but R8 lambdas broken: `new Object`)
     *   enjarify -> enjJar  (correct lambdas/putfields, but drops Signatures)
     *   merge    -> outJar  (enjarify bytecode + dex2jar Signatures)  [+ android fixes + assets]
     * The sidecar runs -Xverify:none, so enjarify's stricter-verifier quirks don't matter.
     *
     * Fallback when enjarify is unavailable: dex2jar + Java-6 downgrade (older extensions only).
     */
    fun convert(apkPath: String, outJarPath: String) {
        val apk = File(apkPath)
        val outJar = File(outJarPath)

        val sigJar = File(outJar.parentFile, outJar.name + ".d2j.jar")
        dex2jar(apk, sigJar)

        val enjJar = File(outJar.parentFile, outJar.name + ".enj.jar")
        val enjOk = runCatching { runEnjarify(apk, enjJar) }.getOrDefault(false)

        try {
            if (enjOk && enjJar.exists() && enjJar.length() > 0) {
                mergeSignatures(sigJar, enjJar, outJar)
                fixAndroidClasses(outJar)
                extractAssets(apk, outJar)
                // no reframe: enjarify output is runtime-correct; verification is off in the sidecar
            } else {
                sigJar.copyTo(outJar, overwrite = true)
                fixAndroidClasses(outJar)
                extractAssets(apk, outJar)
                reframe(outJar)
            }
        } finally {
            sigJar.delete(); enjJar.delete()
        }
    }

    private fun dex2jar(apk: File, out: File) {
        val reader = MultiDexFileReader.open(apk.readBytes())
        val handler = BaksmaliBaseDexExceptionHandler()
        Dex2jar.from(reader)
            .withExceptionHandler(handler)
            .reUseReg(false).topoLogicalSort().skipDebug(true).optimizeSynchronized(false)
            .printIR(false).noCode(false).skipExceptions(false).dontSanitizeNames(true)
            .to(out.toPath())
        if (handler.hasException()) throw RuntimeException("dex2jar reported exceptions for $apk")
    }

    /** Shell out to a bundled enjarify (Python). Returns false if enjarify isn't configured/available. */
    private fun runEnjarify(apk: File, out: File): Boolean {
        val enjDir = System.getenv("RENZO_ENJARIFY_DIR")?.takeIf { File(it).isDirectory } ?: return false
        val py = System.getenv("RENZO_PYTHON") ?: "python3"
        val pb = ProcessBuilder(py, "-O", "-m", "enjarify.main", apk.absolutePath, "-o", out.absolutePath, "-f")
            .directory(File(enjDir))
            .redirectErrorStream(true)
        val p = pb.start()
        val log = p.inputStream.bufferedReader().readText()
        val ok = p.waitFor() == 0 && out.exists() && out.length() > 0
        if (!ok) System.err.println("[enjarify] failed for ${apk.name}: ${log.takeLast(400)}")
        return ok
    }

    /** Inject the generic Signature attributes from [sigJar] into [enjJar]'s bytecode -> [out]. */
    private fun mergeSignatures(sigJar: File, enjJar: File, out: File) {
        val classSig = HashMap<String, String>()
        val methSig = HashMap<String, MutableMap<String, String>>()
        val fieldSig = HashMap<String, MutableMap<String, String>>()
        ZipFile(sigJar).use { zf ->
            val en = zf.entries()
            while (en.hasMoreElements()) {
                val e = en.nextElement()
                if (e.isDirectory || !e.name.endsWith(".class")) continue
                val bytes = zf.getInputStream(e).use { it.readBytes() }
                ClassReader(bytes).accept(object : ClassVisitor(Opcodes.ASM9) {
                    var cn: String? = null
                    override fun visit(v: Int, ac: Int, n: String?, sig: String?, sup: String?, i: Array<String>?) { cn = n; if (sig != null && n != null) classSig[n] = sig }
                    override fun visitField(ac: Int, n: String?, d: String?, sig: String?, value: Any?): FieldVisitor? { if (sig != null && cn != null) fieldSig.getOrPut(cn!!) { HashMap() }["$n::$d"] = sig; return null }
                    override fun visitMethod(ac: Int, n: String?, d: String?, sig: String?, ex: Array<String>?): MethodVisitor? { if (sig != null && cn != null) methSig.getOrPut(cn!!) { HashMap() }["$n::$d"] = sig; return null }
                }, ClassReader.SKIP_CODE)
            }
        }
        val entries = LinkedHashMap<String, ByteArray>()
        ZipFile(enjJar).use { zf ->
            val en = zf.entries()
            while (en.hasMoreElements()) { val e = en.nextElement(); if (!e.isDirectory) entries[e.name] = zf.getInputStream(e).use { it.readBytes() } }
        }
        val outMap = LinkedHashMap<String, ByteArray>()
        for ((name, bytes) in entries) {
            if (!name.endsWith(".class") || !isClass(bytes)) { outMap[name] = bytes; continue }
            outMap[name] = try {
                val cr = ClassReader(bytes)
                val cw = ClassWriter(0)
                cr.accept(object : ClassVisitor(Opcodes.ASM9, cw) {
                    var cn: String? = null
                    override fun visit(v: Int, ac: Int, n: String?, sig: String?, sup: String?, i: Array<String>?) { cn = n; super.visit(v, ac, n, sig ?: classSig[n], sup, i) }
                    override fun visitField(ac: Int, n: String?, d: String?, sig: String?, value: Any?): FieldVisitor? = super.visitField(ac, n, d, sig ?: fieldSig[cn]?.get("$n::$d"), value)
                    override fun visitMethod(ac: Int, n: String?, d: String?, sig: String?, ex: Array<String>?): MethodVisitor = super.visitMethod(ac, n, d, sig ?: methSig[cn]?.get("$n::$d"), ex)
                }, 0)
                cw.toByteArray()
            } catch (_: Throwable) { bytes }
        }
        writeZip(out, outMap)
    }

    // ---- pass 1: android class replacements + namespace removal (frames preserved) ----
    private fun fixAndroidClasses(jar: File) {
        val entries = LinkedHashMap<String, ByteArray>()
        ZipFile(jar).use { zf ->
            val en = zf.entries()
            while (en.hasMoreElements()) {
                val e = en.nextElement()
                if (e.isDirectory) continue
                entries[e.name] = zf.getInputStream(e).use { it.readBytes() }
            }
        }
        val out = LinkedHashMap<String, ByteArray>()
        for ((name, bytes) in entries) {
            if (!name.endsWith(".class") || !isClass(bytes)) { out[name] = bytes; continue }
            val cr = ClassReader(bytes)
            val dotted = cr.className.replace('/', '.')
            if (removeNamespaces.any { dotted.startsWith(it) }) continue // drop
            val cw = ClassWriter(cr, 0)
            cr.accept(ReplacingClassVisitor(cw), 0)
            out[name] = cw.toByteArray()
        }
        writeZip(jar, out)
    }

    // ---- pass 2: merge APK assets/ into jar, drop META-INF ----
    private fun extractAssets(apk: File, jar: File) {
        val existing = LinkedHashMap<String, ByteArray>()
        ZipFile(jar).use { zf ->
            val en = zf.entries()
            while (en.hasMoreElements()) {
                val e = en.nextElement()
                if (e.isDirectory) continue
                if (e.name.startsWith("META-INF/")) continue
                existing[e.name] = zf.getInputStream(e).use { it.readBytes() }
            }
        }
        ZipInputStream(apk.inputStream()).use { zin ->
            var e: ZipEntry? = zin.nextEntry
            while (e != null) {
                val n = e.name
                if (!e.isDirectory && n.startsWith("assets/")) {
                    existing[n] = zin.readBytes()
                }
                e = zin.nextEntry
            }
        }
        writeZip(jar, existing)
    }

    // ---- pass 3: make classes verify on a real JVM regardless of dex2jar's frame quality ----
    //
    // Newer R8 -> dex2jar output ships malformed/absent StackMapTable frames, which the JVM's split
    // verifier (class version >= 51) rejects ("Expecting a stackmap frame..."). Recomputing frames
    // (ASM COMPUTE_FRAMES) needs a complete classpath and silently corrupts classes whose types it
    // can't resolve (widening to Object -> "Bad type on operand stack"). Instead we downgrade every
    // class to Java 6 (major 50) and drop StackMapTable (ClassReader.SKIP_FRAMES): version <= 50
    // makes the JVM use the older type-inference verifier, which needs no frames. maxStack/maxLocals
    // from dex2jar are preserved. dex2jar never emits invokedynamic, so nothing needs v51+.
    private fun reframe(jar: File) {
        val entries = LinkedHashMap<String, ByteArray>()
        ZipFile(jar).use { zf ->
            val en = zf.entries()
            while (en.hasMoreElements()) {
                val e = en.nextElement()
                if (e.isDirectory) continue
                entries[e.name] = zf.getInputStream(e).use { it.readBytes() }
            }
        }
        val out = LinkedHashMap<String, ByteArray>()
        for ((name, bytes) in entries) {
            if (!name.endsWith(".class") || !isClass(bytes)) { out[name] = bytes; continue }
            out[name] = try {
                val cr = ClassReader(bytes)
                val cw = ClassWriter(0) // no COMPUTE; preserve original maxs
                cr.accept(VersionDowngrader(cw), ClassReader.SKIP_FRAMES)
                cw.toByteArray()
            } catch (_: Throwable) {
                bytes
            }
        }
        writeZip(jar, out)
    }

    private class VersionDowngrader(cw: ClassVisitor) : ClassVisitor(Opcodes.ASM9, cw) {
        override fun visit(version: Int, access: Int, name: String?, sig: String?, superName: String?, ifaces: Array<String>?) {
            val major = version and 0xFFFF
            val downgraded = if (major > Opcodes.V1_6) Opcodes.V1_6 else version
            super.visit(downgraded, access, name, sig, superName, ifaces)
        }
    }

    // ---- helpers ----

    private fun isClass(b: ByteArray) = b.size >= 4 &&
        b[0] == 0xCA.toByte() && b[1] == 0xFE.toByte() && b[2] == 0xBA.toByte() && b[3] == 0xBE.toByte()

    private fun writeZip(jar: File, entries: Map<String, ByteArray>) {
        val tmp = File(jar.parentFile, jar.name + ".tmp")
        ZipOutputStream(tmp.outputStream()).use { zos ->
            for ((name, bytes) in entries) {
                zos.putNextEntry(ZipEntry(name))
                zos.write(bytes)
                zos.closeEntry()
            }
        }
        if (jar.exists() && !jar.delete()) throw RuntimeException("could not replace $jar")
        if (!tmp.renameTo(jar)) throw RuntimeException("could not rename ${tmp} -> $jar")
    }

    private fun replaceDirect(s: String?): String? =
        if (s != null && classesToReplace.contains(s)) "$REPLACEMENT_PATH/$s" else s

    private fun replaceIndirect(s: String?): String? {
        if (s == null) return null
        var r = s
        for (c in classesToReplace) r = r!!.replace(c, "$REPLACEMENT_PATH/$c")
        return r
    }

    private class ReplacingClassVisitor(cw: ClassVisitor) : ClassVisitor(Opcodes.ASM9, cw) {
        override fun visitField(a: Int, n: String?, d: String?, s: String?, v: Any?) =
            super.visitField(a, n, replaceIndirect(d), s, v)

        override fun visitMethod(a: Int, n: String?, d: String?, s: String?, ex: Array<String>?): MethodVisitor =
            ReplacingMethodVisitor(super.visitMethod(a, n, replaceIndirect(d) ?: d, s, ex))
    }

    private class ReplacingMethodVisitor(mv: MethodVisitor) : MethodVisitor(Opcodes.ASM9, mv) {
        override fun visitTypeInsn(op: Int, type: String?) = super.visitTypeInsn(op, replaceDirect(type))
        override fun visitMethodInsn(op: Int, owner: String?, name: String?, desc: String?, itf: Boolean) =
            super.visitMethodInsn(op, replaceDirect(owner), name, replaceIndirect(desc), itf)
        override fun visitFieldInsn(op: Int, owner: String?, name: String?, desc: String?) =
            super.visitFieldInsn(op, owner, name, replaceIndirect(desc))
    }

}
