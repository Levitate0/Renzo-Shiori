import org.objectweb.asm.*;
import java.io.*;
import java.util.*;
import java.util.zip.*;

// Merges generic Signature attributes from a dex2jar-produced jar (which preserves them) into an
// enjarify-produced jar (which has correct lambdas/putfields but drops signatures). Both translate
// the same DEX, so class + member names match. Result: correct bytecode AND generic signatures,
// which injekt's TypeReference and kotlinx-serialization reified types need.
public class MergeSig {
    // className -> class signature
    static Map<String,String> classSig = new HashMap<>();
    // className -> (name+desc -> signature) for methods and fields
    static Map<String,Map<String,String>> methSig = new HashMap<>();
    static Map<String,Map<String,String>> fieldSig = new HashMap<>();

    public static void main(String[] a) throws Exception {
        String dexJar = a[0], enjJar = a[1], outJar = a[2];
        // 1) harvest signatures from the dex2jar jar
        eachClass(dexJar, (name, bytes) -> {
            new ClassReader(bytes).accept(new ClassVisitor(Opcodes.ASM9) {
                String cn;
                public void visit(int v,int ac,String n,String sig,String sup,String[] i){ cn=n; if(sig!=null) classSig.put(n,sig); }
                public FieldVisitor visitField(int ac,String n,String d,String sig,Object val){ if(sig!=null) fieldSig.computeIfAbsent(cn,k->new HashMap<>()).put(n+"::"+d,sig); return null; }
                public MethodVisitor visitMethod(int ac,String n,String d,String sig,String[] ex){ if(sig!=null) methSig.computeIfAbsent(cn,k->new HashMap<>()).put(n+"::"+d,sig); return null; }
            }, ClassReader.SKIP_CODE);
        });
        System.out.println("harvested: "+classSig.size()+" class sigs, "+methSig.size()+" classes w/ method sigs, "+fieldSig.size()+" w/ field sigs");

        // 2) rewrite enjarify jar, injecting signatures
        int[] injected = {0};
        ZipOutputStream zout = new ZipOutputStream(new FileOutputStream(outJar));
        ZipInputStream zin = new ZipInputStream(new FileInputStream(enjJar));
        ZipEntry e;
        while ((e = zin.getNextEntry()) != null) {
            byte[] bytes = zin.readAllBytes();
            byte[] out = bytes;
            if (e.getName().endsWith(".class")) {
                try {
                    ClassReader cr = new ClassReader(bytes);
                    ClassWriter cw = new ClassWriter(0);
                    cr.accept(new SigInjector(cw, injected), 0);
                    out = cw.toByteArray();
                } catch (Throwable t) { /* keep original */ }
            }
            zout.putNextEntry(new ZipEntry(e.getName()));
            zout.write(out);
            zout.closeEntry();
        }
        zin.close(); zout.close();
        System.out.println("injected "+injected[0]+" signatures -> "+outJar);
    }

    static class SigInjector extends ClassVisitor {
        String cn; int[] cnt;
        SigInjector(ClassVisitor cv,int[] c){ super(Opcodes.ASM9, cv); cnt=c; }
        public void visit(int v,int ac,String n,String sig,String sup,String[] i){
            cn=n; String s=classSig.get(n); if(s!=null && sig==null){ sig=s; cnt[0]++; }
            super.visit(v,ac,n,sig,sup,i);
        }
        public FieldVisitor visitField(int ac,String n,String d,String sig,Object val){
            if(sig==null){ Map<String,String> m=fieldSig.get(cn); if(m!=null){ String s=m.get(n+"::"+d); if(s!=null){ sig=s; cnt[0]++; } } }
            return super.visitField(ac,n,d,sig,val);
        }
        public MethodVisitor visitMethod(int ac,String n,String d,String sig,String[] ex){
            if(sig==null){ Map<String,String> m=methSig.get(cn); if(m!=null){ String s=m.get(n+"::"+d); if(s!=null){ sig=s; cnt[0]++; } } }
            return super.visitMethod(ac,n,d,sig,ex);
        }
    }

    interface CB { void accept(String name, byte[] bytes) throws Exception; }
    static void eachClass(String jar, CB cb) throws Exception {
        ZipInputStream z = new ZipInputStream(new FileInputStream(jar));
        ZipEntry e;
        while ((e=z.getNextEntry())!=null) if (e.getName().endsWith(".class")) cb.accept(e.getName(), z.readAllBytes());
        z.close();
    }
}
