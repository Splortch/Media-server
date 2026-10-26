#!/usr/bin/env python3
"""Keep required checks present, but skip expensive steps for unrelated changes."""
import json,os,subprocess,sys
kind=sys.argv[1];event=json.load(open(os.environ['GITHUB_EVENT_PATH']))
base=event.get('pull_request',{}).get('base',{}).get('sha') or event.get('before')
head=event.get('pull_request',{}).get('head',{}).get('sha') or os.environ['GITHUB_SHA']
common=('Directory.Build.props','global.json','dotnet-tools.json','.editorconfig','.gitattributes','scripts/component-changes.py')
prefixes={'backend':('backend/','scripts/audit.py','.github/workflows/backend-server.yml'),'web':('frontend/','shared/api/','.github/workflows/web-client.yml'),'windows':('clients/windows/','shared/api/','backend/','frontend/','scripts/test-native-client.py','.github/workflows/windows-client.yml')}
changed=True
if base and set(base)!={'0'}:
    result=subprocess.run(['git','diff','--name-only',base,head],capture_output=True,text=True)
    if result.returncode==0:changed=any(p.startswith(prefixes[kind]) or p in common for p in result.stdout.splitlines())
with open(os.environ['GITHUB_OUTPUT'],'a') as out:out.write('changed='+str(changed).lower()+'\n')
print(kind+': '+('validate changed dependencies' if changed else 'no affected component changes'))
