#!/usr/bin/env python3
"""Controlled mail smoke checks. Credentials and message bodies never enter output."""
import argparse, email, imaplib, importlib.util, json, smtplib, socket, ssl, time, uuid
from email.message import EmailMessage
from email.utils import formatdate, make_msgid
from pathlib import Path
ROOT=Path('/root/proyect/naperu-mail')
HOST='mail.naperu.cloud'
IP='72.61.37.46'
def production_contract():
    spec=importlib.util.spec_from_file_location('smtp_probe_mail_ops',Path(__file__).with_name('mail-ops.py'))
    ops=importlib.util.module_from_spec(spec);spec.loader.exec_module(ops)
    return {'exposure':ops.check_public_ports(),'smtp_network':ops.check_smtp_network(require_mail=True)}
def probe_endpoints(public):
    # Browser HTTPS is public; authenticated mail endpoints remain private.
    return {'smtp':('127.0.0.1',465 if public else 2465),
            'imap':('127.0.0.1',993 if public else 2993),
            'transfer':(IP if public else '127.0.0.1',25 if public else 2525)}
class SMTP(smtplib.SMTP_SSL):
    def _get_socket(self, host, port, timeout):
        raw=socket.create_connection((host,port),timeout)
        return self.context.wrap_socket(raw,server_hostname=HOST)
class IMAP(imaplib.IMAP4_SSL):
    def _create_socket(self, timeout):
        raw=socket.create_connection((self.host,self.port),timeout)
        return self.ssl_context.wrap_socket(raw,server_hostname=HOST)
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--public',action='store_true');parser.add_argument('--recipient');args=parser.parse_args()
    creds=json.loads((ROOT/'.local/credentials.json').read_text())
    context=ssl.create_default_context() if args.public else ssl.create_default_context(cafile=str(ROOT/'certs/fullchain.pem'))
    endpoints=probe_endpoints(args.public);endpoint,smtpport=endpoints['smtp'];imapport=endpoints['imap'][1];transferhost,transferport=endpoints['transfer']
    result=[]
    def good(name): result.append({'check':name,'passed':True})
    try:
        contract=production_contract() if args.public else None
        with SMTP(endpoint,smtpport,context=context,timeout=10) as client:
            client.login('notificaciones@naperu.cloud',creds['notificaciones@naperu.cloud']);good('tls_authentication')
        with SMTP(endpoint,smtpport,context=context,timeout=10) as client:
            try: client.login('notificaciones@naperu.cloud','invalid-password-for-negative-test')
            except smtplib.SMTPAuthenticationError: good('invalid_password_rejected')
            else: raise RuntimeError('invalid password accepted')
        with smtplib.SMTP(transferhost,transferport,timeout=15) as client:
            client.ehlo('probe.naperu.cloud');client.mail('outside@example.com');code,_=client.rcpt('outside@example.net')
            if code<400: raise RuntimeError('unauthenticated relay accepted')
            good('unauthenticated_external_relay_rejected')
        with SMTP(endpoint,smtpport,context=context,timeout=15) as client:
            client.login('notificaciones@naperu.cloud',creds['notificaciones@naperu.cloud']);code,_=client.mail('spoof@example.net')
            if code<400: code,_=client.rcpt('soporte@naperu.cloud')
            if code<400: raise RuntimeError('sender spoof accepted')
            good('authenticated_sender_spoof_rejected')
        if not args.public:
            try:
                raw=socket.create_connection((endpoint,smtpport),timeout=3);raw.settimeout(3);raw.sendall(b'EHLO probe.naperu.cloud\r\n');reply=raw.recv(200)
                if reply.startswith(b'250') or reply.startswith(b'220'): raise RuntimeError('plaintext SMTP accepted on TLS port')
            except (socket.timeout,ConnectionError): pass
            finally:
                if 'raw' in locals():raw.close()
            good('plaintext_on_tls_port_rejected')
        marker='Naperu SMTP QA '+uuid.uuid4().hex
        message=EmailMessage();message['From']='Acrópolis Channel <notificaciones@naperu.cloud>';message['To']='soporte@naperu.cloud';message['Subject']=marker;message['Date']=formatdate(localtime=False);message['Message-ID']=make_msgid(domain='naperu.cloud')
        message.set_content('Prueba técnica controlada de correo de Acrópolis Channel. No contiene enlaces ni datos de usuarios.')
        with SMTP(endpoint,smtpport,context=context,timeout=30) as client:
            client.login('notificaciones@naperu.cloud',creds['notificaciones@naperu.cloud']);client.send_message(message)
        with IMAP(endpoint,imapport,ssl_context=context,timeout=15) as mailbox:
            mailbox.login('soporte@naperu.cloud',creds['soporte@naperu.cloud']);mailbox.select('INBOX')
            identifiers=[]
            for _ in range(15):
                typ, data=mailbox.uid('search',None,'HEADER','Subject','"'+marker+'"');identifiers=data[0].split()
                if identifiers:break
                time.sleep(1)
            if len(identifiers)!=1:raise RuntimeError('controlled message not received')
            typ,data=mailbox.uid('fetch',identifiers[0],'(BODY.PEEK[])');parsed=email.message_from_bytes(data[0][1])
            signature=parsed.get('DKIM-Signature','')
            if 'd=naperu.cloud' not in signature or 's=mail202610' not in signature:raise RuntimeError('DKIM signing not present')
            good('local_delivery_imap_with_dkim')
            mailbox.uid('store',identifiers[0],'+FLAGS','(\\Deleted)');mailbox.uid('EXPUNGE',identifiers[0])
        if args.recipient:
            if not args.public or args.recipient!='ricardo.rojas.campos@gmail.com':raise RuntimeError('external send requires public TLS and designated address')
            message.replace_header('To',args.recipient);message.replace_header('Subject','Acrópolis Channel — prueba SMTP propia')
            with SMTP(endpoint,smtpport,context=context,timeout=30) as client:
                client.login('notificaciones@naperu.cloud',creds['notificaciones@naperu.cloud']);client.send_message(message)
            good('designated_external_message_queued_not_yet_delivered')
        report={'status':'passed','public':args.public,'connection_contract':contract,'checks':result,'created_at_utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())}
        target=ROOT/'.local'/('smtp-public-report.json' if args.public else 'smtp-stage-report.json');target.write_text(json.dumps(report,indent=2));target.chmod(0o600)
        print(json.dumps(report))
    except Exception as error:
        # Do not print server responses or messages with potentially sensitive contents.
        print(json.dumps({'status':'failed','checks':result,'failed_type':type(error).__name__,'reason':str(error) if isinstance(error,RuntimeError) else 'server response omitted'}));raise SystemExit(1)
if __name__=='__main__':main()
