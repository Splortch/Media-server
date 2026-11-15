#!/usr/bin/env python3
"""Exercise the real Gradle locking policy against an isolated local Maven repo."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def publish(repository, name, version, versions, dependency=''):
    directory = repository / 'org/example' / name / version
    directory.mkdir(parents=True, exist_ok=True)
    dependencies = (f'<dependencies><dependency><groupId>org.example</groupId><artifactId>{dependency}</artifactId>'
                    '<version>1.+</version></dependency></dependencies>') if dependency else ''
    (directory / f'{name}-{version}.pom').write_text(
        f'<project><modelVersion>4.0.0</modelVersion><groupId>org.example</groupId>'
        f'<artifactId>{name}</artifactId><version>{version}</version>{dependencies}</project>')
    with zipfile.ZipFile(directory / f'{name}-{version}.jar', 'w') as archive:
        archive.writestr('fixture.txt', 'Synthetic dependency; contains no executable code.')
    (directory.parent / 'maven-metadata.xml').write_text(
        f'<metadata><groupId>org.example</groupId><artifactId>{name}</artifactId><versioning>'
        f'<latest>{versions[-1]}</latest><release>{versions[-1]}</release><versions>'
        + ''.join(f'<version>{v}</version>' for v in versions)
        + '</versions><lastUpdated>20261114000000</lastUpdated></versioning></metadata>')


def verify(gradle):
    with tempfile.TemporaryDirectory(prefix='android-lock-policy-') as temporary:
        root = Path(temporary)
        (root / 'settings.gradle.kts').write_text('rootProject.name = "lock-policy-fixture"\ninclude(":app", ":core")\n')
        policy = (ROOT / 'clients/android/dependency-locks.gradle.kts').as_posix()
        (root / 'build.gradle.kts').write_text(f'apply(from = "{policy}")\n')
        for module in ('app', 'core'):
            directory = root / module
            directory.mkdir()
            (directory / 'build.gradle.kts').write_text('''
repositories { maven { url = uri(rootProject.file("repository")) } }
for (variant in listOf("debugCompileClasspath", "debugRuntimeClasspath", "releaseRuntimeClasspath")) {
    configurations.create(variant) { isCanBeResolved = true; isCanBeConsumed = false }
    dependencies.add(variant, "org.example:parent:1.+")
}
tasks.named("resolveDependencyLocks") {
    doLast {
        val selected = configurations.filter { it.isCanBeResolved }.flatMap { configuration ->
            configuration.incoming.resolutionResult.allComponents.map { it.id }
                .filterIsInstance<org.gradle.api.artifacts.component.ModuleComponentIdentifier>()
                .map { it.displayName }
        }.distinct().sorted()
        file("resolved.txt").writeText(selected.joinToString("\\n"))
    }
}
''')
        repository = root / 'repository'
        publish(repository, 'leaf', '1.0', ['1.0'])
        publish(repository, 'parent', '1.0', ['1.0'], dependency='leaf')
        tasks = [':app:resolveDependencyLocks', ':core:resolveDependencyLocks']

        def run(*options, success=True):
            result = subprocess.run(
                [str(gradle), '--no-daemon', '--max-workers=2', '-p', str(root), *tasks, *options],
                capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=180)
            output = result.stdout + result.stderr
            if success and result.returncode != 0:
                raise AssertionError('Gradle locking fixture failed:\n' + output[-6000:])
            if not success and (result.returncode == 0 or 'lock state' not in output.lower()):
                raise AssertionError('Expected lock-state rejection:\n' + output[-6000:])

        def locks():
            return {module: (root / module / 'gradle.lockfile').read_bytes() for module in ('app', 'core')}

        def selected():
            return [(root / module / 'resolved.txt').read_text().splitlines() for module in ('app', 'core')]

        run('--write-locks')
        initial = locks()
        assert all(b'org.example:leaf:1.0=' in value for value in initial.values())
        assert all('org.example:leaf:1.0' in versions for versions in selected())
        # New metadata cannot silently upgrade a previously locked transitive input.
        publish(repository, 'leaf', '1.1', ['1.0', '1.1'])
        run('--refresh-dependencies')
        assert locks() == initial, 'Ordinary resolution rewrote the committed lock state'
        assert all('org.example:leaf:1.0' in versions and 'org.example:leaf:1.1' not in versions for versions in selected())
        # An explicit update must observe the new version, proving the fixture's
        # updated repository metadata was effective rather than hidden by caching.
        run('--refresh-dependencies', '--update-locks', 'org.example:leaf')
        updated = locks()
        assert all(b'org.example:leaf:1.1=' in value for value in updated.values())
        assert all(b'org.example:parent:1.0=' in value for value in updated.values())
        assert all('org.example:leaf:1.1' in versions and 'org.example:leaf:1.0' not in versions for versions in selected())
        print('PASS: transitive versions remain pinned until an explicit update.', flush=True)

        missing = root / 'app/gradle.lockfile'
        missing.unlink()
        run(success=False)
        assert not missing.exists(), 'Missing lock state was silently recreated'
        missing.write_bytes(updated['app'])
        print('PASS: missing lock state fails without silently generating a lockfile.', flush=True)

        publish(repository, 'extra', '1.0', ['1.0'])
        app = root / 'app/build.gradle.kts'
        app.write_text(app.read_text() + '\ndependencies.add("debugRuntimeClasspath", "org.example:extra:1.0")\n')
        run(success=False)
        assert locks() == updated, 'Rejected graph drift modified the lockfiles'
        print('PASS: an unreviewed dependency addition fails without modifying lock state.', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--gradle', type=Path, default=ROOT / 'clients/android' / ('gradlew.bat' if os.name == 'nt' else 'gradlew'))
    args = parser.parse_args()
    verify(args.gradle.resolve())
