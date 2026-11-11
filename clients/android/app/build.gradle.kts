import groovy.json.JsonOutput
import org.gradle.api.artifacts.component.ModuleComponentIdentifier
import org.gradle.api.artifacts.result.ResolvedArtifactResult
import org.gradle.maven.MavenModule
import org.gradle.maven.MavenPomArtifact

plugins {
    id("com.android.application")
    kotlin("plugin.compose")
}

val buildNumber =
    providers.gradleProperty("androidBuildNumber").orElse("1").get().toInt().also {
        require(it in 1..65535)
    }

android {
    namespace = "org.splortch.mediaserver.android"
    compileSdk = 37
    defaultConfig {
        applicationId = "org.splortch.mediaserver.android"
        minSdk = 26
        targetSdk = 36
        versionCode = buildNumber
        versionName = "0.1.0-alpha.$buildNumber"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }
    buildTypes {
        debug { applicationIdSuffix = ".debug" }
        release { isMinifyEnabled = false }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures {
        compose = true
        buildConfig = true
    }
}

dependencies {
    implementation(project(":core"))
    implementation(platform("androidx.compose:compose-bom:2026.09.00"))
    implementation("androidx.activity:activity-compose:1.11.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.9.4")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.4")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.core:core-ktx:1.19.1")
    implementation("androidx.media3:media3-exoplayer:1.9.4")
    implementation("androidx.media3:media3-exoplayer-hls:1.9.4")
    implementation("androidx.media3:media3-ui:1.9.4")
    implementation("androidx.media3:media3-datasource-okhttp:1.9.4")
    implementation("androidx.media3:media3-session:1.9.4")
    androidTestImplementation(platform("androidx.compose:compose-bom:2026.09.00"))
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    // Compose testing still pulls Espresso 3.5; API 37 removed its reflective
    // InputManager.getInstance call. 3.7 uses Context.getSystemService instead.
    androidTestImplementation("androidx.test.espresso:espresso-core:3.7.0")
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("androidx.test:runner:1.7.0")
    debugImplementation("androidx.compose.ui:ui-test-manifest")
}

kotlin { compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) } }

// Inventory the resolved application runtime, independently for debug/release.
// Instrumentation, JVM tests and compiler/plugin classpaths are not app inputs.
androidComponents.onVariants { variant ->
    val runtime = configurations.named("${variant.name}RuntimeClasspath")
    val runtimeArtifacts = runtime.map { configuration ->
        configuration.incoming.artifactView {
            // First-party Android projects expose several artifact types. Their
            // code is covered by the project license; retain external archives.
            componentFilter { it is ModuleComponentIdentifier }
        }.artifacts
    }
    val generatedAssets = objects.directoryProperty()
    generatedAssets.set(layout.buildDirectory.dir("generated/dependencyInventory/${variant.name}"))
    val generator = rootProject.file("../../scripts/android-inventory.py")
    val report = layout.buildDirectory.dir("reports/dependencyInventory/${variant.name}")
    val commit = providers.environmentVariable("GITHUB_SHA").orElse(
        providers.exec { commandLine("git", "rev-parse", "HEAD") }.standardOutput.asText.map { it.trim() }
    )
    val python = providers.gradleProperty("inventoryPython").orElse("python3")
    val task = tasks.register<Exec>("generate${variant.name.replaceFirstChar { it.uppercase() }}DependencyInventory") {
        notCompatibleWithConfigurationCache("Exports resolved Maven artifacts and POMs for license review")
        inputs.file(generator)
        inputs.files(runtimeArtifacts.map { it.artifactFiles }).withPathSensitivity(PathSensitivity.NONE)
        inputs.property("sourceCommit", commit)
        inputs.property("variant", variant.name)
        inputs.property("version", "0.1.0-alpha.$buildNumber")
        outputs.dir(generatedAssets)
        outputs.dir(report)
        // POM/license evidence must be refreshed even when compiled code is cached.
        outputs.upToDateWhen { false }
        doFirst {
            val configuration = runtime.get()
            val modules = configuration.incoming.resolutionResult.allComponents
                .map { it.id }.filterIsInstance<ModuleComponentIdentifier>()
            require(modules.isNotEmpty()) { "Expected Android runtime dependencies" }
            val query = project.dependencies.createArtifactResolutionQuery()
                .forComponents(modules)
                .withArtifacts(MavenModule::class.java, MavenPomArtifact::class.java)
                .execute()
            require(query.resolvedComponents.map { it.id }.toSet() == modules.toSet()) {
                "Every Android runtime component needs its exact resolved POM"
            }
            val poms = query.resolvedComponents.associate { component ->
                val pom = component.getArtifacts(MavenPomArtifact::class.java)
                    .filterIsInstance<ResolvedArtifactResult>().single()
                component.id to pom.file
            }
            val archives = runtimeArtifacts.get().artifacts
                // Multiple selected variants can refer to the very same AAR.
                .distinctBy { it.file.canonicalFile }
                .groupBy { it.id.componentIdentifier }
            val components = modules.sortedBy { "${it.group}:${it.module}:${it.version}" }.map { id ->
                mapOf(
                    "group" to id.group, "name" to id.module, "version" to id.version,
                    "pom" to poms.getValue(id).absolutePath,
                    "archives" to archives[id].orEmpty().sortedBy { it.file.name }.map {
                        mapOf("name" to it.file.name, "file" to it.file.absolutePath)
                    },
                )
            }
            val graph = temporaryDir.resolve("runtime.json")
            graph.writeText(JsonOutput.toJson(mapOf(
                "variant" to variant.name, "version" to "0.1.0-alpha.$buildNumber",
                "commit" to commit.get(), "components" to components,
            )))
            // Only this task's generated asset directory is replaced. No cache
            // inventory is trusted, and Python publishes only validated output.
            generatedAssets.get().asFile.deleteRecursively()
            commandLine(python.get(), generator.absolutePath, "--graph", graph.absolutePath,
                "--output", generatedAssets.get().asFile.resolve("inventory").absolutePath)
        }
        doLast {
            report.get().asFile.deleteRecursively()
            generatedAssets.get().asFile.resolve("inventory").copyRecursively(report.get().asFile)
        }
    }
    variant.sources.assets!!.addGeneratedSourceDirectory(task) { generatedAssets }
}
