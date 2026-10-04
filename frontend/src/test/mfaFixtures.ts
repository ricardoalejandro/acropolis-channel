import type { MfaChallenge } from '../api/identity';
export function challenge(enrollmentRequired = false): MfaChallenge {
  return {
    mfaRequired: true,
    enrollmentRequired,
    challengeToken: 'test-challenge-only',
    expiresUtc: new Date(Date.now() + 300000).toISOString(),
  };
}
export function enrollment() {
  return {
    challengeToken: 'test-enrollment-only',
    sharedKey: 'JBSWY3DPEHPK3PXP',
    authenticatorUri: 'otpauth://totp/Acropolis:test?secret=JBSWY3DPEHPK3PXP&issuer=Acropolis',
    expiresUtc: new Date(Date.now() + 300000).toISOString(),
  };
}
export const recoveryCodes = Array.from({ length: 10 }, (_, index) => 'QA-RECOVERY-' + index);
