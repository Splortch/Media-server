import org.gradle.api.artifacts.dsl.LockMode
import org.gradle.api.artifacts.result.UnresolvedDependencyResult

subprojects {
    dependencyLocking { lockMode.set(LockMode.STRICT) }
    configurations.configureEach {
        if (name.endsWith("CompileClasspath") || name.endsWith("RuntimeClasspath")) {
            resolutionStrategy.activateDependencyLocking()
        }
    }
    tasks.register("resolveDependencyLocks") {
        description = "Resolve every Android application/test compile and runtime graph against its lock state."
        group = "verification"
        notCompatibleWithConfigurationCache("Enumerates and resolves Android variant configurations")
        doLast {
            val classpaths = configurations.filter {
                it.isCanBeResolved && (it.name.endsWith("CompileClasspath") || it.name.endsWith("RuntimeClasspath"))
            }.sortedBy { it.name }
            require(classpaths.isNotEmpty()) { "Expected Android variant classpaths" }
            classpaths.forEach { configuration ->
                // Resolve the graph, not an ambiguous choice of first-party AAR
                // artifacts. This also covers variants not compiled by this run.
                val failures = configuration.incoming.resolutionResult.allDependencies
                    .filterIsInstance<UnresolvedDependencyResult>()
                if (failures.isNotEmpty()) {
                    throw GradleException("Locked graph ${project.path}:${configuration.name} failed", failures.first().failure)
                }
                logger.lifecycle("Verified locked graph ${project.path}:${configuration.name}")
            }
        }
    }
}
