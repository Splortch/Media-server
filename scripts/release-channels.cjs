'use strict';
// Central release-channel validation. All channels are public, but only stable is a normal release.
const TYPES = Object.freeze(['alpha', 'beta', 'stable']);
function parseVersion(value, family) {
  const match = /^(0\.(?:1|2)\.\d+)(?:-(alpha|beta)\.([1-9]\d{0,4}))?$/.exec(value);
  if (!match || (family === 'server' && !match[1].startsWith('0.2.')) ||
      (family === 'client' && !match[1].startsWith('0.1.'))) throw new Error('Invalid release version: ' + value);
  return {version:value, base:match[1], channel:match[2] || 'stable', sequence:match[3] ? Number(match[3]) : null};
}
function channelFor(server, client) {
  const s = parseVersion(server, 'server'), c = parseVersion(client, 'client');
  if (s.channel !== c.channel || s.sequence !== c.sequence) throw new Error('Server and client release channels must match.');
  return s.channel;
}
function canPromote(from, to) {
  return (from === 'alpha' && to === 'beta') || (from === 'beta' && to === 'stable');
}
module.exports = {TYPES, parseVersion, channelFor, canPromote};
