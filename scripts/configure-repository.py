#!/usr/bin/env python3
"""Apply confirmed repository settings with an already-authenticated GitHub CLI."""
import argparse, json, subprocess, sys
parser=argparse.ArgumentParser();parser.add_argument('--repo',default='Splortch/Media-server');parser.add_argument('--project',action='store_true');args=parser.parse_args()
owner,repo=args.repo.split('/')
failures=[]
def gh(*arguments, payload=None):
    result=subprocess.run(['gh',*arguments],input=json.dumps(payload) if payload is not None else None,text=True,capture_output=True)
    if result.returncode: raise RuntimeError(result.stderr.strip())
    if not result.stdout.strip(): return None
    try: return json.loads(result.stdout)
    except json.JSONDecodeError: return result.stdout
def attempt(name,fn):
    try: fn();print('Applied:',name)
    except (RuntimeError,OSError) as e: failures.append(name);print('BLOCKED:',name,str(e),file=sys.stderr)
gh('auth','status')
attempt('public visibility and project metadata',lambda: gh('repo','edit',args.repo,'--visibility','public','--accept-visibility-change-consequences','--description','A modern, open-source, self-hosted media server focused on performance, simplicity, and a seamless streaming experience.','--enable-discussions','--enable-squash-merge','--delete-branch-on-merge'))
attempt('Dependabot alerts',lambda: gh('api','--method','PUT',f'repos/{args.repo}/vulnerability-alerts'))
attempt('automated security fixes',lambda: gh('api','--method','PUT',f'repos/{args.repo}/automated-security-fixes'))
attempt('main protection',lambda: gh('api','--method','PUT',f'repos/{args.repo}/branches/main/protection','--input','-',payload={'required_status_checks':{'strict':True,'contexts':['Backend (ubuntu-24.04)','Backend (windows-latest)','Frontend','End-to-end','Docker build']},'enforce_admins':False,'required_pull_request_reviews':{'required_approving_review_count':1,'dismiss_stale_reviews':True},'restrictions':None,'required_conversation_resolution':True,'allow_force_pushes':False,'allow_deletions':False}))
attempt('private vulnerability reporting',lambda: gh('api','--method','PUT',f'repos/{args.repo}/private-vulnerability-reporting'))
def project():
    projects=gh('project','list','--owner',owner,'--format','json')['projects']; title='Media Server development'
    selected=next((p for p in projects if p['title']==title),None)
    is_new=selected is None
    selected=selected or gh('project','create','--owner',owner,'--title',title,'--format','json')
    number=str(selected['number']); fields=gh('project','field-list',number,'--owner',owner,'--format','json')['fields']; status=next(x for x in fields if x['name']=='Status')
    query='mutation($id:ID!,$options:[ProjectV2SingleSelectFieldOptionInput!]!){updateProjectV2Field(input:{fieldId:$id,singleSelectOptions:$options}){projectV2Field{... on ProjectV2SingleSelectField{id}}}}'
    colors=['GRAY','BLUE','YELLOW','PURPLE','GREEN'];names=['Backlog','Ready','In Progress','In Review','Done']
    if [o['name'] for o in status.get('options',[])] != names:
        if not is_new: raise RuntimeError('Existing project uses other statuses; review it manually before replacing options.')
        gh('api','graphql','--input','-',payload={'query':query,'variables':{'id':status['id'],'options':[{'name':name,'color':color,'description':name} for name,color in zip(names,colors)]}})
    fields=gh('project','field-list',number,'--owner',owner,'--format','json')['fields'];status=next(x for x in fields if x['name']=='Status');backlog=next(x['id'] for x in status['options'] if x['name']=='Backlog')
    issues=gh('issue','list','--repo',args.repo,'--limit','100','--json','url')
    existing=gh('project','item-list',number,'--owner',owner,'--format','json','--limit','100')['items']; urls={x.get('content',{}).get('url') for x in existing}
    for issue in issues:
        if issue['url'] not in urls:
            item=gh('project','item-add',number,'--owner',owner,'--url',issue['url'],'--format','json')
            gh('project','item-edit','--id',item['id'],'--project-id',selected['id'],'--field-id',status['id'],'--single-select-option-id',backlog)
    print('Project:',selected.get('url',number));print('Choose a Board view grouped by Status in the project UI; view layout is not configured by this helper.')
if args.project:attempt('project board',project)
if failures:print('Unfinished operations:',', '.join(failures));sys.exit(1)
