// JetBrains AI through Grazie, which answers browser origins. Its models are Claude's, served at Grazie's Anthropic route; a
// request carries the licence token in `grazie-authenticate-jwt`, and Grazie's preflight allows only that, Content-Type and
// Authorization, so the SDK's other headers and fields are dropped on the way out. Ported from the ThinkRail tab host.
export const JETBRAINS = 'jetbrains-ai';
export const GRAZIE_BASE = 'https://api.jetbrains.ai';
export const GRAZIE_ANTHROPIC = `${GRAZIE_BASE}/user/v5/llm/anthropic`;
const JWT_HEADER = 'grazie-authenticate-jwt';

function expiry(jwt) {
  try { return JSON.parse(atob(jwt.split('.')[1].replace(/-/g, '+').replace(/_/g, '/'))).exp * 1000; } catch { return 0; }
}

/** True for a token that is shaped like a JWT and has not expired; a licence token lasts an hour at most. */
export function jwtLive(token) {
  return typeof token === 'string' && /^[\w-]+\.[\w-]+\.[\w-]+$/.test(token) && expiry(token) > Date.now();
}

// A Claude id carries its version in either order (`claude-3-5-sonnet`, `claude-sonnet-4-5`) and maybe a date: [3, 5], [4, 5].
function versionOf(id) {
  return (id.replace(/-\d{8}$/, '').match(/-(\d+)(?=-|$)/g) ?? []).map(part => Number(part.slice(1)));
}

function newer(a, b) {
  const [x, y] = [versionOf(a.id), versionOf(b.id)];
  for (let i = 0; i < Math.max(x.length, y.length); i++) if ((x[i] ?? 0) !== (y[i] ?? 0)) return (y[i] ?? 0) - (x[i] ?? 0);
  return 0;
}

/** The Claude models of the catalog, addressed to JetBrains AI, newest first (the picker leads with the first of each family). */
export function jetbrainsModels(claudeModels) {
  return [...claudeModels].sort(newer).map(model => ({ ...model, provider: JETBRAINS, baseUrl: GRAZIE_ANTHROPIC }));
}

function withoutUnsupportedFields(body) {
  if (typeof body !== 'string') return body;
  try {
    const { output_config, messages, thinking, ...rest } = JSON.parse(body);
    const { block_binding, ...plainThinking } = thinking ?? {};
    return JSON.stringify({
      ...rest,
      ...(thinking ? { thinking: plainThinking } : {}),
      messages: Array.isArray(messages) ? messages.filter(message => !('output_config' in message)) : messages
    });
  } catch { return body; }
}

/** Calls to Grazie's Anthropic route are rewritten as it accepts them; every other call goes through untouched. */
export function grazieFetch(native) {
  return (input, init) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    if (!url.startsWith(GRAZIE_ANTHROPIC)) return native(input, init);
    const original = new Headers(init?.headers);
    const jwt = original.get('x-api-key') ?? original.get('authorization')?.replace(/^Bearer /i, '');
    const headers = new Headers();
    const contentType = original.get('content-type');
    if (contentType) headers.set('content-type', contentType);
    if (jwt) headers.set(JWT_HEADER, jwt);
    const body = withoutUnsupportedFields(init?.body);
    return native(input, { ...init, headers, ...(body == null ? {} : { body }) });
  };
}
