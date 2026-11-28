export const maximumPermitSeconds = 48 * 3600

function decode(value) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_-]+$/.test(value)) throw new Error('Invalid permit encoding.')
  return Uint8Array.from(atob(value.replaceAll('-', '+').replaceAll('_', '/')), character => character.charCodeAt(0))
}

export async function verifyStationPermit(envelope, key, accountId, assignment) {
  if (!key || !assignment?.entryOpen) return null
  try {
    const publicKey = await crypto.subtle.importKey('spki', decode(key),
      { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify'])
    const encoded = decode(envelope.payload)
    if (!await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' },
      publicKey, decode(envelope.signature), encoded)) return null
    const claims = JSON.parse(new TextDecoder().decode(encoded))
    if (typeof claims.id !== 'string' || claims.operatorId !== accountId ||
      claims.eventId !== assignment.eventId || claims.checkpointId !== assignment.checkpointId ||
      claims.generation !== assignment.generation || claims.policyRevision !== assignment.offlinePolicyRevision ||
      !Number.isSafeInteger(claims.issuedAt) || !Number.isSafeInteger(claims.expiresAt) ||
      claims.expiresAt <= claims.issuedAt || claims.expiresAt - claims.issuedAt > maximumPermitSeconds ||
      !Number.isFinite(Date.parse(assignment.operationalStartUtc)) ||
      !Number.isFinite(Date.parse(assignment.operationalEndUtc)) ||
      claims.issuedAt * 1000 < Math.floor(Date.parse(assignment.operationalStartUtc) / 1000) * 1000 ||
      claims.expiresAt * 1000 > Date.parse(assignment.operationalEndUtc)) return null
    return claims
  } catch {
    return null
  }
}

export function permitTime(permit, wallNow, monotonicNow) {
  if (!Number.isFinite(permit.lastWall) || !Number.isFinite(permit.lastVerifiedTime) ||
    wallNow < permit.lastWall || wallNow < permit.anchorWall) return Infinity
  return Math.max(wallNow, permit.lastVerifiedTime,
    permit.anchorTime + Math.max(0, monotonicNow - permit.anchorMonotonic))
}
