<?php
$config['use_https'] = getenv('NAPERU_MAIL_PUBLIC') === '1';
$config['session_samesite'] = 'Lax';
$config['product_name'] = 'Correo Naperu';
$config['language'] = 'es_ES';
$config['smtp_user'] = '%u';
$config['smtp_pass'] = '%p';
$config['imap_conn_options'] = ['ssl' => ['verify_peer' => true, 'verify_peer_name' => true, 'cafile' => '/run/naperu-mail/ca-bundle.pem']];
$config['smtp_conn_options'] = $config['imap_conn_options'];
$config['session_lifetime'] = 20;
$config['login_autocomplete'] = 0;
$config['log_logins'] = false;
$config['enable_installer'] = false;
$config['smtp_debug'] = false;
$config['imap_debug'] = false;
$config['login_lc'] = 2;
$config['max_message_size'] = '20M';
$config['quota_zero_as_unlimited'] = false;
?>
