// Generates X-Twilio-Signature test vectors with Twilio's OFFICIAL Node library, so the C#
// implementation is checked against an independent reference rather than against itself.
//   npm i twilio@5.13.1 && node tools/twilio-vectors.js
const { getExpectedTwilioSignature, validateRequest } = require('twilio/lib/webhooks/webhooks');

const cases = [
  {
    token: '12345',
    url: 'https://mycompany.com/myapp.php?foo=1&bar=2',
    params: { CallSid: 'CA1234567890ABCDE', Caller: '+12349013030', Digits: '1234', From: '+12349013030', To: '+18005551212' },
  },
  {
    token: 'b5f1c0ffee0ddba11deadbeef0123456',
    url: 'https://voice.example.com/voice/incoming',
    params: { AccountSid: 'AC00000000000000000000000000000000', CallSid: 'CA11111111111111111111111111111111', From: '+17752528333', To: '+13237466888', CallStatus: 'ringing', Direction: 'inbound' },
  },
];

const out = cases.map(c => ({ ...c, signature: getExpectedTwilioSignature(c.token, c.url, c.params) }));

// Twilio signs the URL WITHOUT a non-standard port; the request arrives WITH it.
const signedUrl = 'https://voice.example.com/voice/incoming';
const arrivedUrl = 'https://voice.example.com:8443/voice/incoming';
const signature = getExpectedTwilioSignature(cases[1].token, signedUrl, cases[1].params);
out.push({
  token: cases[1].token, url: arrivedUrl, params: cases[1].params, signature,
  officialLibAcceptsArrivedUrl: validateRequest(cases[1].token, signature, arrivedUrl, cases[1].params),
});

console.log(JSON.stringify(out, null, 2));
