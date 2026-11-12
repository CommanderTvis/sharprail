// Pi inside the tab: pi-agent-core's agent loop behind the same line-delimited JSON commands and events as
// `pi --mode rpc`, working in the tab's own file system and Git. Bundled into vendor/tabpi.js by browser-assets.cs.
import { Agent } from '@mariozechner/pi-agent-core';
import { bashTool } from './shell.mjs';
import { JETBRAINS, grazieFetch, jetbrainsModels, jwtLive } from './jetbrains.mjs';
import { Type, fauxAssistantMessage, fauxText, fauxToolCall, getModels, getProviders, registerFauxProvider } from '@mariozechner/pi-ai';

/** pi-ai's scripted provider, for checks that drive the agent without a key. */
export { createTerminals } from './shell.mjs';
/** The bundled pi-agent-core's version, set when the page's assets are built. */
export const version = typeof PI_VERSION === 'string' ? PI_VERSION : 'dev';
export const faux = { assistant: fauxAssistantMessage, text: fauxText, toolCall: fauxToolCall, register: registerFauxProvider };

const LEVELS = ['off', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'];
const PREFERRED = { anthropic: 'claude-sonnet-4-5', openai: 'gpt-5', google: 'gemini-2.5-pro' };
const GIT_COMMANDS = ['status', 'diff', 'log', 'add', 'rm', 'restore', 'commit', 'branch', 'show', 'ls-files', 'rev-parse'];

const text = value => ({ content: [{ type: 'text', text: value }], details: {} });
const decoder = new TextDecoder();

// A provider "key" that is an http(s) URL is a gateway: JetBrains Central's local proxy (`central add pi`) takes requests with
// no key of its own. Browsers may send it only the headers it allows, so the SDK's extra ones are dropped on the way.
const GATEWAY = /^https?:\/\/(127\.0\.0\.1|localhost)(:\d+)?\//;
const GATEWAY_HEADERS = ['authorization', 'content-type', 'accept'];
let gatewayFetch = false;
function allowGatewayRequests() {
  if (gatewayFetch) return;
  gatewayFetch = true;
  const native = grazieFetch(globalThis.fetch.bind(globalThis));
  globalThis.fetch = (input, init) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    if (!GATEWAY.test(url) || !url.includes('/wire/')) return native(input, init);
    const kept = new Headers();
    for (const [name, value] of new Headers(init?.headers ?? (typeof input === 'object' && 'headers' in input ? input.headers : undefined))) {
      if (GATEWAY_HEADERS.includes(name.toLowerCase())) kept.set(name, value);
    }
    return native(input, { ...init, headers: kept });
  };
}

// pi-ai names the Claude models that think adaptively (Opus 4.6 and up, Sonnet 4.6) and sends every other id the budget form,
// which newer models refuse. The provider's own list says how a model thinks and which effort levels it takes (`thinking` and
// `efforts` on the live entry); with no list, a model is judged by its version number.
const EFFORT_ORDER = ['low', 'medium', 'high', 'xhigh', 'max'];
const BUDGET_LEVELS = ['low', 'medium', 'high'];

/** The levels the model offers: off when it can run without thinking, then its effort levels (or budget sizes). */
export function effortsOf(capabilities) {
  if (!capabilities) return undefined;
  const levels = capabilities.thinking?.types?.disabled?.supported === false ? [] : ['off'];
  if (capabilities.effort?.supported) levels.push(...EFFORT_ORDER.filter(level => capabilities.effort[level]?.supported));
  else if (capabilities.thinking?.types?.enabled?.supported) levels.push(...BUDGET_LEVELS);
  return levels;
}

/** The effort a chat level asks of a model: the level itself when the model takes it, else the closest it does. */
function effortFor(level, efforts) {
  const wanted = level === 'minimal' ? 'low' : level;
  const takes = (efforts ?? EFFORT_ORDER).filter(effort => effort !== 'off');
  if (takes.includes(wanted)) return wanted;
  const at = EFFORT_ORDER.indexOf(wanted);
  return takes.reduce((best, effort) => best === undefined || Math.abs(EFFORT_ORDER.indexOf(effort) - at) < Math.abs(EFFORT_ORDER.indexOf(best) - at) ? effort : best, undefined) ?? 'medium';
}

export function adaptiveFor(model, params, level) {
  if (model?.provider !== 'anthropic' || params?.thinking?.type !== 'enabled') return params;
  let adaptive = model.adaptive;
  if (adaptive === undefined) {
    const version = /(?:opus|sonnet|haiku)-(\d+)(?:[-.](\d{1,2})(?!\d))?/.exec(model.id ?? '');
    adaptive = !!version && (Number(version[1]) > 4 || (Number(version[1]) === 4 && Number(version[2] ?? 0) >= 6));
  }
  if (!adaptive) return params;
  const { budget_tokens, ...rest } = params.thinking;
  return { ...params, thinking: { ...rest, type: 'adaptive' }, output_config: { ...params.output_config, effort: effortFor(level, model.efforts) } };
}

/** Builds the agent factory over the tab's file system (Emscripten FS) and its git command line. */
export function createPi({ FS, gitRun, authPath, model: override, ...git }) {
  allowGatewayRequests();
  function keys() {
    try { return JSON.parse(decoder.decode(FS.readFile(authPath))); } catch { return {}; }
  }
  const rawKey = provider => keys()[provider]?.key;
  const gatewayOf = provider => GATEWAY.test(rawKey(provider) ?? '') ? rawKey(provider) : null;
  // A gateway answers for the provider itself, so the model is pointed at it and given a placeholder key.
  const keyOf = provider => gatewayOf(provider) ? 'wire-proxy' : rawKey(provider);
  const routed = model => model && gatewayOf(model.provider) ? { ...model, baseUrl: gatewayOf(model.provider) } : model;

  function available() {
    const known = keys();
    const ids = getProviders().filter(provider => known[provider]?.type === 'api_key' && known[provider].key);
    // JetBrains AI answers with Claude's models; its token lasts an hour, so an expired one is no provider.
    return jwtLive(known[JETBRAINS]?.key) ? [...ids, JETBRAINS] : ids;
  }

  // The bundled catalog trails the providers: Anthropic's own list says which models a key can use today. A model it
  // names that the catalog lacks is described by the newest catalog model of the same family.
  const live = new Map();
  async function loadLive(provider) {
    const key = rawKey(provider);
    if (provider !== 'anthropic' || !key || gatewayOf(provider)) return;
    if (live.get(provider)?.key === key) return;
    let models = null;
    try {
      const response = await fetch('https://api.anthropic.com/v1/models?limit=100', {
        headers: { 'x-api-key': key, 'anthropic-version': '2023-06-01', 'anthropic-dangerous-direct-browser-access': 'true' }
      });
      if (response.ok) {
        const catalog = getModels(provider);
        const family = id => ['opus', 'sonnet', 'haiku'].find(name => id.includes(name));
        models = (await response.json()).data.map(entry => ({
          ...(catalog.find(model => model.id === entry.id)
            ?? { ...(catalog.filter(model => model.id.includes(entry.line ?? family(entry.id) ?? '\0')).at(-1) ?? catalog.at(-1)), id: entry.id, name: entry.display_name ?? entry.id }),
          efforts: effortsOf(entry.capabilities),
          adaptive: entry.capabilities?.thinking?.types ? entry.capabilities.thinking.types.adaptive?.supported === true : undefined
        }));
      }
    } catch { /* offline or blocked: the catalog stands */ }
    live.set(provider, { key, models });
  }
  const modelsOf = provider => provider === JETBRAINS ? jetbrainsModels(getModels('anthropic')) : live.get(provider)?.models ?? getModels(provider);

  function pickModel(wanted) {
    if (override) return override;
    if (wanted) {
      const [provider, ...rest] = wanted.split('/');
      try { const model = modelsOf(provider).find(model => model.id === rest.join('/')); if (model) return routed(model); } catch { /* unknown */ }
    }
    const [provider] = available();
    if (!provider) return null;
    const models = modelsOf(provider);
    // A live list is newest first.
    if (live.get(provider)?.models) return routed(models.find(model => model.id.includes('sonnet')) ?? models[0] ?? null);
    return routed(models.find(model => model.id === PREFERRED[provider]) ?? models.find(model => model.id.startsWith(PREFERRED[provider] ?? '\0')) ?? models[0] ?? null);
  }

  function tools(cwd) {
    const inside = path => {
      const parts = [];
      for (const part of String(path).split('/')) {
        if (part === '' || part === '.') continue;
        if (part === '..') { if (!parts.length) throw new Error(`path escapes the workspace: ${path}`); parts.pop(); } else parts.push(part);
      }
      return [cwd, ...parts].join('/');
    };
    const mkdirs = dir => {
      let current = '';
      for (const part of dir.split('/').filter(Boolean)) { current += '/' + part; try { FS.mkdir(current); } catch { /* exists */ } }
    };
    const read = path => decoder.decode(FS.readFile(inside(path)));
    const list = (path, depth) => {
      const lines = [];
      const walk = (dir, level) => {
        for (const name of FS.readdir(dir).filter(name => name !== '.' && name !== '..' && name !== '.git').sort()) {
          const full = dir + '/' + name;
          const directory = FS.isDir(FS.stat(full).mode);
          lines.push('  '.repeat(level) + name + (directory ? '/' : ''));
          if (directory && level + 1 < depth) walk(full, level + 1);
        }
      };
      walk(inside(path), 0);
      return lines;
    };
    return [
      {
        name: 'read_file', label: 'Read file', description: 'Read a UTF-8 text file; paths are relative to the workspace root.',
        parameters: Type.Object({ path: Type.String() }),
        execute: async (_id, { path }) => text(read(path))
      },
      {
        name: 'write_file', label: 'Write file', description: 'Create or overwrite a UTF-8 text file, creating parent directories.',
        parameters: Type.Object({ path: Type.String(), content: Type.String() }),
        execute: async (_id, { path, content }) => {
          const full = inside(path);
          mkdirs(full.slice(0, full.lastIndexOf('/')));
          FS.writeFile(full, new TextEncoder().encode(content));
          return text(`wrote ${content.length} characters to ${path}`);
        }
      },
      {
        name: 'edit_file', label: 'Edit file', description: 'Replace one exact occurrence of old_text with new_text in a file.',
        parameters: Type.Object({ path: Type.String(), old_text: Type.String(), new_text: Type.String() }),
        execute: async (_id, { path, old_text, new_text }) => {
          const current = read(path);
          const first = current.indexOf(old_text);
          if (first < 0) throw new Error('old_text was not found');
          if (current.indexOf(old_text, first + 1) >= 0) throw new Error('old_text occurs more than once; include more context');
          FS.writeFile(inside(path), new TextEncoder().encode(current.slice(0, first) + new_text + current.slice(first + old_text.length)));
          return text(`edited ${path}`);
        }
      },
      {
        name: 'list_dir', label: 'List directory', description: 'List a directory as an indented tree (depth defaults to 2); "." is the workspace root.',
        parameters: Type.Object({ path: Type.String(), depth: Type.Optional(Type.Number()) }),
        execute: async (_id, { path, depth }) => text(list(path, depth ?? 2).join('\n') || '(empty)')
      },
      {
        name: 'delete_file', label: 'Delete file', description: 'Delete a file.',
        parameters: Type.Object({ path: Type.String() }),
        execute: async (_id, { path }) => { FS.unlink(inside(path)); return text(`deleted ${path}`); }
      },
      bashTool({ FS, gitRun, ...git }, cwd, Type),
      {
        name: 'git', label: 'Git', description: `Run git in the workspace. Supported: ${GIT_COMMANDS.join(', ')}. Pass the arguments after "git", e.g. ["commit","-m","message"].`,
        parameters: Type.Object({ args: Type.Array(Type.String()) }),
        execute: async (_id, { args }) => {
          if (!GIT_COMMANDS.includes(args[0])) throw new Error(`git ${args[0]} is not available here; use one of: ${GIT_COMMANDS.join(', ')}`);
          const result = await gitRun(cwd, args, null);
          const output = decoder.decode(result.out);
          if (result.exit !== 0) throw new Error((result.err || output || `git exited ${result.exit}`).trim());
          return text(output.trim() || '(no output)');
        }
      }
    ];
  }

  /** One conversation: write commands with `send`, read events and responses with `next`. */
  return function open({ cwd, model: wanted, thinking }) {
    const queue = [];
    const waiters = [];
    let closed = false;
    const emit = value => {
      const line = JSON.stringify(value);
      if (waiters.length) waiters.shift()(line); else queue.push(line);
    };
    const respond = (id, command, data, error) => emit(error === undefined ? { type: 'response', id, command, success: true, data } : { type: 'response', id, command, success: false, error });

    // pi-agent-core fills in a placeholder model when given none, so whether a real one is chosen is tracked here.
    let selected = pickModel(wanted);
    // The level the chat shows; pi-agent-core has no "max", so it carries "xhigh" in its place and the request says "max".
    let chosen = LEVELS.includes(thinking) ? thinking : 'off';
    const toAgentLevel = level => level === 'max' ? 'xhigh' : level;
    // A model takes only some levels: the shown one moves to the closest it takes when the model changes.
    const fit = model => {
      const takes = model?.efforts;
      if (!takes || takes.includes(chosen)) return;
      chosen = chosen === 'off' || !takes.some(level => level !== 'off') ? (takes.includes('off') ? 'off' : takes[0] ?? 'off') : effortFor(chosen, takes);
      agent.state.thinkingLevel = toAgentLevel(chosen);
    };
    const agent = new Agent({
      initialState: {
        systemPrompt: `You are Pi, a coding agent running inside a browser tab. Work in the workspace at the root of the tab's own file system with the tools provided; paths are relative to that root. The bash tool is a simulated shell (coreutils, rg, jq, git), not a real machine. Use the git tool for version control.`,
        model: selected ?? undefined,
        thinkingLevel: toAgentLevel(chosen),
        tools: tools(cwd)
      },
      getApiKey: provider => keyOf(provider),
      onPayload: (params, model) => adaptiveFor(model, params, chosen)
    });
    agent.subscribe(event => emit(event));
    const sessionId = crypto.randomUUID();

    // A key added in Settings after this chat opened still counts, and a model the key's provider no longer lists is
    // replaced: the choice is made again while there is none or it went stale.
    async function choose() {
      for (const provider of available()) await loadLive(provider);
      const listed = selected && live.get(selected.provider)?.models;
      const stale = !!listed && !listed.some(model => model.id === selected.id);
      if (!selected || stale) {
        selected = pickModel(stale ? undefined : wanted);
        if (selected) agent.state.model = selected;
      }
      // The live entry carries what the provider says the model can do, which a catalog copy lacks.
      const entry = selected && listed?.find(model => model.id === selected.id);
      if (entry && selected.efforts === undefined) { selected = routed(entry); agent.state.model = selected; }
      fit(selected);
      return selected;
    }

    const describe = model => model ? { provider: model.provider, id: model.id, name: model.name ?? model.id, contextWindow: model.contextWindow, reasoning: !!model.reasoning, efforts: model.efforts } : null;

    async function handle(command) {
      const { id, type } = command;
      try {
        switch (type) {
          case 'get_state':
            return respond(id, type, { model: describe(await choose()), thinkingLevel: chosen, isStreaming: agent.state.isStreaming, sessionId, sessionFile: '', sessionName: '' });
          case 'get_available_models':
            for (const provider of available()) await loadLive(provider);
            return respond(id, type, { models: available().flatMap(provider => modelsOf(provider).map(describe)) });
          case 'get_commands':
            return respond(id, type, { commands: [] });
          case 'get_messages':
            return respond(id, type, { messages: agent.state.messages });
          case 'set_thinking_level':
            if (!LEVELS.includes(command.level)) throw new Error('unknown thinking level');
            chosen = command.level;
            agent.state.thinkingLevel = toAgentLevel(chosen);
            return respond(id, type, {});
          case 'set_model': {
            const model = modelsOf(command.provider).find(model => model.id === command.modelId);
            if (!model) throw new Error('unknown model');
            selected = routed(model);
            agent.state.model = selected;
            fit(model);
            return respond(id, type, describe(model));
          }
          case 'abort':
            agent.abort();
            return respond(id, type, {});
          case 'prompt': {
            if (!await choose()) throw new Error('No model is available. Add an API key in Settings › Model providers.');
            if (agent.state.isStreaming) {
              agent.steer({ role: 'user', content: [{ type: 'text', text: command.message }], timestamp: Date.now() });
              return respond(id, type, {});
            }
            respond(id, type, {});
            await agent.prompt(command.message, command.images);
            return undefined;
          }
          default:
            return respond(id, type, undefined, `Unsupported command: ${type}`);
        }
      } catch (error) {
        return respond(id, type, undefined, error?.message ?? String(error));
      }
    }

    return {
      send(line) { void handle(JSON.parse(line)); },
      next() {
        if (queue.length) return Promise.resolve(queue.shift());
        if (closed) return Promise.resolve(null);
        return new Promise(resolve => waiters.push(resolve));
      },
      close() {
        closed = true;
        agent.abort();
        for (const waiter of waiters.splice(0)) waiter(null);
      }
    };
  };
}
