export function validateCorpus(corpus) {
  if (!Array.isArray(corpus.cases) || !corpus.cases.length) throw Error('No corpus cases');
  const ids = new Set();
  for (const scenario of corpus.cases) {
    if (!/^[a-z]+$/.test(scenario.server) || ids.has(scenario.server)) throw Error('Invalid or duplicate provider');
    ids.add(scenario.server);
    if (!/^[\w.-]+\/[\w.-]+$/.test(scenario.repository) || !/^[a-f0-9]{40}$/.test(scenario.revision)) throw Error('A repository and immutable commit are required');
    if (!scenario.path || scenario.path.startsWith('/') || scenario.path.split(/[\\/]/).includes('..')) throw Error('Probe must stay inside the project');
    if (!(scenario.minimumSourceFiles >= 25) || !scenario.extensions?.length) throw Error('A medium-sized source floor is required');
    if (!scenario.calls?.some(call => call.assert === 'locations') || !scenario.calls.some(call => call.assert === 'checked')) throw Error('Startup alone is not a semantic check');
    for (const call of scenario.calls) {
      if (!['lsp_status', 'lsp_navigate', 'lsp_diagnostics'].includes(call.tool)) throw Error('Only language MCP calls belong in this suite');
      if (call.assert && !['locations', 'checked', 'crossFile', 'protocol'].includes(call.assert)) throw Error('Unknown assertion');
      if (call.anchor && !call.anchor.text.includes(call.anchor.identifier)) throw Error('Invalid symbol anchor');
    }
  }
}
