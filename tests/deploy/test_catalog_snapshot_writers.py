from pathlib import Path
import shlex, subprocess, unittest
from test_verify_restore import bash_function
ROOT=Path(__file__).resolve().parents[2]
CID='a'*64
CID2='b'*64
class CatalogueSnapshotWriterTests(unittest.TestCase):
 def probe(self, project='acropolis_test_snapshot', database=None, containers=CID, states=None, compose_exit=0, inspect_exit=0):
  states=states or {CID:'acropolis_test_snapshot web false'}
  cases='\n'.join(shlex.quote(k)+') printf %s '+shlex.quote(v)+' ;;' for k,v in states.items())
  script='set -Eeuo pipefail\n'+bash_function((ROOT/'scripts/verify.sh').read_text(),'assert_catalog_writers_stopped')+'\n'
  script+='QA_PROJECT='+shlex.quote(project)+'\nQA_DATABASE='+shlex.quote(database or project)+'\n'
  script+='compose() { printf %s '+shlex.quote(containers)+'; return '+str(compose_exit)+'; }\n'
  script+='docker() { case "${@: -1}" in\n'+cases+'\n*) return 3 ;; esac; return '+str(inspect_exit)+'; }\nassert_catalog_writers_stopped\n'
  return subprocess.run(['bash','--noprofile','--norc','-c',script],env={'PATH':'/usr/bin:/bin','LC_ALL':'C'},capture_output=True,text=True,timeout=5).returncode
 def test_owned_stopped_writer_accepted(self):self.assertEqual(self.probe(),0)
 def test_running_writer_blocks(self):self.assertEqual(self.probe(states={CID:'acropolis_test_snapshot web true'}),1)
 def test_foreign_project_blocks(self):self.assertEqual(self.probe(states={CID:'foreign web false'}),1)
 def test_wrong_service_blocks(self):self.assertEqual(self.probe(states={CID:'acropolis_test_snapshot db false'}),1)
 def test_one_active_of_two_blocks(self):self.assertEqual(self.probe(containers=CID+'\n'+CID2,states={CID:'acropolis_test_snapshot web false',CID2:'acropolis_test_snapshot web true'}),1)
 def test_absent_before_first_start_accepted(self):self.assertEqual(self.probe(containers=''),0)
 def test_compose_error_not_absence(self):self.assertEqual(self.probe(containers='',compose_exit=17),1)
 def test_inspect_error_not_stopped(self):self.assertEqual(self.probe(inspect_exit=17),1)
 def test_production_rejected(self):self.assertEqual(self.probe(project='acropolis-channel'),2)
 def test_database_mismatch_rejected(self):self.assertEqual(self.probe(database='acropolis'),2)
 def test_bad_identity_rejected(self):self.assertEqual(self.probe(containers='invalid'),2)
if __name__=='__main__':unittest.main()
