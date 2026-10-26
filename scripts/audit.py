#!/usr/bin/env python3
"""Fail on any reported direct/transitive NuGet vulnerability."""
import json, subprocess, sys
result = subprocess.run(['dotnet', 'list', 'MediaServer.sln', 'package', '--vulnerable', '--include-transitive', '--format', 'json', '--no-restore'], capture_output=True, text=True)
if result.returncode:
    print(result.stdout, result.stderr); sys.exit(result.returncode)
report = json.loads(result.stdout)
issues = []
for project in report.get('projects', []):
    for framework in project.get('frameworks', []):
        for group in ['topLevelPackages', 'transitivePackages']:
            for package in framework.get(group, []):
                if package.get('vulnerabilities'): issues.append((project['path'], package))
for issue in issues: print(json.dumps(issue))
print(f'NuGet audit: {len(issues)} vulnerable packages')
sys.exit(bool(issues))
