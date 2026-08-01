# Tink (pulled in via androidx.security:security-crypto) references Google's
# errorprone annotations, which are compile-only and absent at runtime — R8
# treats the missing classes as an error without these.
-dontwarn com.google.errorprone.annotations.**

# kotlinx.serialization: keep the generated serializer lookups for our DTOs.
-keepclassmembers class app.renzoshiori.client.data.model.** {
    *** Companion;
}
-keepclasseswithmembers class app.renzoshiori.client.data.model.** {
    kotlinx.serialization.KSerializer serializer(...);
}
