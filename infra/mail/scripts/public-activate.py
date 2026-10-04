#!/usr/bin/env python3
"""Publish only the independent mail stack after exact external prerequisites."""
import argparse, hashlib, json, os, re, shutil, subprocess, tempfile
from pathlib import Path
import yaml
ROOT=Path('/root/proyect/naperu-mail');SOURCE=Path(__file__).resolve().parents[1]
ROUTE=Path('/etc/dokploy/traefik/dynamic/naperu-mail.yml')
def run(args):return subprocess.check_output(args,text=True,stderr=subprocess.DEVNULL).strip()
def dns(all_records):
    expected=json.loads((ROOT/'.local/dns-required.json').read_text())
    for resolver in ['8.8.8.8','1.1.1.1']:
        for item in expected:
            if not all_records and item['type']!='A':continue
            name='naperu.cloud' if item['name']=='@' else item['name']+'.naperu.cloud'
            raw=run(['dig','@'+resolver,name,item['type'],'+short']);lines=raw.splitlines()
            if item['type']=='TXT':lines=[''.join(re.findall(r'"([^"]*)"',line)) for line in lines]
            value=str(item['priority'])+' '+item['value']+'.' if item['type']=='MX' else item['value']
            if value not in lines:raise RuntimeError('DNS pending: '+item['type']+' '+name)
        for host in ['mail.naperu.cloud','webmail.naperu.cloud']:
            if run(['dig','@'+resolver,host,'AAAA','+short']):raise RuntimeError('Unexpected AAAA: '+host)
        if all_records and run(['dig','@'+resolver,'-x','72.61.37.46','+short'])!='mail.naperu.cloud.':raise RuntimeError('PTR pending')
def atomic(path,data,mode):
    fd,name=tempfile.mkstemp(dir=path.parent,prefix='.naperu-mail-')
    try:
        os.fchmod(fd,mode)
        with os.fdopen(fd,'wb') as handle:handle.write(data);handle.flush();os.fsync(handle.fileno())
        os.replace(name,path)
    finally:
        if os.path.exists(name):os.unlink(name)
def main():
    parser=argparse.ArgumentParser();parser.add_argument('operation',choices=['routing','publish','allow-delivery']);args=parser.parse_args()
    os.umask(0o077)
    dns(args.operation!='routing')
    if args.operation=='routing':
        candidate=yaml.safe_load((SOURCE/'public-routing.yml').read_bytes())
        for router in candidate['http']['routers'].values():router['service']='noop@internal'
        data=yaml.safe_dump(candidate,sort_keys=False).encode()
        names=set(candidate['http']['routers'])|set(candidate['http']['services'])|set(candidate['http']['middlewares'])
        for file in ROUTE.parent.glob('*.yml'):
            if file==ROUTE:continue
            existing=yaml.safe_load(file.read_bytes()) or {};section=existing.get('http',{})
            if names.intersection(set(section.get('routers',{}))|set(section.get('services',{}))|set(section.get('middlewares',{}))):raise RuntimeError('Routing collision')
        snapshot=ROOT/'.local/routing-before.yml'
        if ROUTE.exists() and not snapshot.exists():shutil.copyfile(ROUTE,snapshot);snapshot.chmod(0o600)
        atomic(ROUTE,data,0o644)
        print(json.dumps({'routing_prepared':True,'certificate_pending':True,'public_smtp':False}));return
    ops=SOURCE/'scripts/mail-ops.py'
    run(['python3',str(ops),'check-public'])
    run(['python3',str(ops),'export-cert'])
    if args.operation=='publish':
        # Removing the staging CA is mandatory before this browser service is public.
        atomic(ROOT/'roundcube-trust/ca-bundle.pem',Path('/etc/ssl/certs/ca-certificates.crt').read_bytes(),0o644)
        command=['docker','compose','--env-file','runtime.env','-f','compose.yml','-f','public.yml','up','-d']
        subprocess.run(command,cwd=ROOT,check=True)
        atomic(ROUTE,(SOURCE/'public-routing.yml').read_bytes(),0o644)
        (ROOT/'.local/public-started').write_text('published ports; external delivery still deferred\n')
        print(json.dumps({'public_ports_started':True,'external_delivery_still_deferred':True}));return
    run(['python3',str(ops),'check-public','--mail-tls'])
    conf=ROOT/'config/postfix-main.cf';original=conf.read_text()
    data=re.sub(r'^defer_transports\s*=.*$', 'defer_transports =', original,flags=re.M)
    atomic(conf,data.encode(),0o600)
    run(['docker','exec','naperu-mail-mailserver-1','postconf','-e','defer_transports='])
    run(['docker','exec','naperu-mail-mailserver-1','postfix','reload'])
    print(json.dumps({'external_delivery_enabled':True,'application_email_unchanged':True}))
if __name__=='__main__':
    try:main()
    except (RuntimeError,subprocess.CalledProcessError) as exc:
        print(json.dumps({'status':'blocked','reason':str(exc) if isinstance(exc,RuntimeError) else 'Required operation failed'}));raise SystemExit(1)
