plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "com.mgrscreen.viewer"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.mgrscreen.viewer"
        minSdk = 29
        targetSdk = 35
        versionCode = 5
        versionName = "1.0.0"
    }

    buildFeatures { compose = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    implementation(platform("androidx.compose:compose-bom:2024.10.01"))
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.lifecycle:lifecycle-viewmodel-ktx:2.8.7")
    implementation("androidx.datastore:datastore-preferences:1.1.1")
    testImplementation("junit:junit:4.13.2")
}

val projectRoot = rootProject.layout.projectDirectory.dir("..")
val licenseAssets = layout.buildDirectory.dir("generated/licenseAssets")
val copyLicenseNotices by tasks.registering(Sync::class) {
    from(projectRoot.file("LICENSE"), projectRoot.file("THIRD_PARTY_NOTICES.md"))
    from(projectRoot.dir("licenses"))
    into(licenseAssets.map { it.dir("licenses") })
}
android.sourceSets["main"].assets.srcDir(licenseAssets)
tasks.named("preBuild") {
    dependsOn(copyLicenseNotices)
}
