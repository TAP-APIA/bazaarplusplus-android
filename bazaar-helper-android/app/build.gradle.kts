plugins { id("com.android.application") }
android {
    namespace = "com.bazzarhelper.android"
    compileSdk = 36
    defaultConfig { applicationId = "com.bazzarhelper.android"; minSdk = 29; targetSdk = 35; versionCode = 10; versionName = "0.1.11"; ndk { abiFilters += "arm64-v8a" } }
    signingConfigs { getByName("debug") { storeFile = rootProject.file(".keystore/development.p12"); storePassword = "android"; keyAlias = "bazaarhelper"; keyPassword = "android" } }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    sourceSets["main"].assets.srcDir(rootProject.file("build/assets"))
    sourceSets["main"].jniLibs.srcDir(rootProject.file("build/native"))
    packaging { jniLibs.useLegacyPackaging = true }
}
// The SDK-only command prepares pinned runtime/native inputs without compiling upstream launcher code.
val prepareRuntime by tasks.registering(Exec::class) {
    workingDir(rootProject.projectDir)
    commandLine("python3", "tools/build.py", "--interop", providers.gradleProperty("gameInterop").orElse(".local/game-interop").get())
}
tasks.named("preBuild").configure { dependsOn(prepareRuntime) }

tasks.matching { it.name == "validateSigningDebug" }.configureEach { dependsOn(prepareRuntime) }
