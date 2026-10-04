#!/usr/bin/env python3
"""Prepare private mail service; never enable public ports or application email."""
import json, os, secrets, shutil, subprocess
from pathlib import Path
ROOT=Path('/root/proyect/naperu-mail')
SOURCE=Path(__file__).resolve().parents[1]
def run(args, **kw): return subprocess.run(args,check=True,capture_output=True,**kw)
def private(path,content):
    path.write_text(content); path.chmod(0o600)
def main():
    os.umask(0o077)
    if (ROOT/'.local/public-started').exists(): raise SystemExit('Service already public; use the documented maintenance procedure, not initial provisioning.')
    ROOT.mkdir(exist_ok=True,mode=0o700)
    for name in ['.local','config','config/rspamd/override.d','config/rspamd/dkim','certs/versions/stage','mail-data','mail-state','mail-logs','roundcube-db','roundcube-config','roundcube-trust']:
        (ROOT/name).mkdir(parents=True,exist_ok=True)
    images=json.loads((ROOT/'.local/images.json').read_text())
    lock={i['requested']:i['digest'] for i in images}
    private(ROOT/'runtime.env','MAILSERVER_IMAGE='+lock['ghcr.io/docker-mailserver/docker-mailserver:16.0.1']+'\nROUNDCUBE_IMAGE='+lock['roundcube/roundcubemail:1.7.4-apache']+'\n')
    (SOURCE/'images.lock.json').write_text(json.dumps(images,indent=2)+'\n')
    for name in ['compose.yml','stage.yml','public.yml']: shutil.copyfile(SOURCE/name, ROOT/name)
    for name in ['postfix-main.cf','postfix-virtual.cf','user-patches.sh']:
        target=ROOT/'config'/name
        if not target.exists(): shutil.copyfile(SOURCE/name,target)
    (ROOT/'config/user-patches.sh').chmod(0o755)
    shutil.copyfile(SOURCE/'rspamd-dkim.conf',ROOT/'config/rspamd/override.d/dkim_signing.conf')
    shutil.copyfile(SOURCE/'rspamd-controller.conf',ROOT/'config/rspamd/override.d/worker-controller.inc')
    shutil.copyfile(SOURCE/'roundcube.inc.php',ROOT/'roundcube-config/mail.inc.php')
    credentials=ROOT/'.local/credentials.json'
    if not credentials.exists():
        accounts={name:secrets.token_urlsafe(30) for name in ['notificaciones@naperu.cloud','soporte@naperu.cloud']}
        private(credentials,json.dumps(accounts))
    else: accounts=json.loads(credentials.read_text())
    passwd=ROOT/'config/postfix-accounts.cf'
    if not passwd.exists():
        entries=[]
        for email,password in accounts.items():
            hashed=run(['openssl','passwd','-6','-stdin'],input=(password+'\n').encode()).stdout.decode().strip()
            entries.append(email+'|{SHA512-CRYPT}'+hashed)
        private(passwd,'\n'.join(entries)+'\n')
    # The configuration is traversable inside this container; files with secrets
    # remain inaccessible on the host through ROOT mode 0700.
    (ROOT/'roundcube-config').chmod(0o755)
    (ROOT/'roundcube-trust').chmod(0o755)
    des=ROOT/'roundcube-config/security.inc.php'
    if not des.exists(): private(des,"<?php $config['des_key'] = '"+secrets.token_hex(12)+"'; ?>\n")
    for file in (ROOT/'roundcube-config').glob('*.php'): file.chmod(0o644)
    certdir=ROOT/'certs/versions/stage'
    if not (certdir/'fullchain.pem').exists():
        run(['openssl','req','-x509','-newkey','rsa:3072','-nodes','-days','14','-keyout',str(certdir/'privkey.pem'),'-out',str(certdir/'fullchain.pem'),'-subj','/CN=mail.naperu.cloud/O=Naperu staging only','-addext','subjectAltName=DNS:mail.naperu.cloud,DNS:localhost','-addext','basicConstraints=critical,CA:TRUE'])
        (certdir/'privkey.pem').chmod(0o600)
        (certdir/'fullchain.pem').chmod(0o600)
    for name,dest in [('current','versions/stage'),('fullchain.pem','current/fullchain.pem'),('privkey.pem','current/privkey.pem')]:
        target=ROOT/'certs'/name
        if not target.exists() and not target.is_symlink(): target.symlink_to(dest)
    bundle=Path('/etc/ssl/certs/ca-certificates.crt').read_bytes()+(certdir/'fullchain.pem').read_bytes()
    (ROOT/'roundcube-trust/ca-bundle.pem').write_bytes(bundle)
    (ROOT/'roundcube-trust/ca-bundle.pem').chmod(0o644)
    key=ROOT/'config/rspamd/dkim/naperu.cloud.mail202610.key'
    if not key.exists():
        generated=run(['docker','run','--rm','--entrypoint','rspamadm',lock['ghcr.io/docker-mailserver/docker-mailserver:16.0.1'],'dkim_keygen','-b','2048','-s','mail202610','-d','naperu.cloud']).stdout.decode()
        end=generated.index('-----END PRIVATE KEY-----')+len('-----END PRIVATE KEY-----')
        private(key,generated[:end]+'\n')
        private(ROOT/'.local/dkim-public.txt',generated[end:].strip()+'\n')
    # Rspamd worker needs to read its key. Bind mount uses the image's numeric GID.
    gid=run(['docker','run','--rm','--entrypoint','getent',lock['ghcr.io/docker-mailserver/docker-mailserver:16.0.1'],'group','_rspamd']).stdout.decode().split(':')[2]
    os.chown(key,0,int(gid));key.chmod(0o640)
    (ROOT/'roundcube-db').chmod(0o750); os.chown(ROOT/'roundcube-db',33,33)
    print(json.dumps({'prepared':True,'public':False,'accounts':list(accounts),'pinned_images':True}))
if __name__=='__main__': main()
