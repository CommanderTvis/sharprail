import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { validateCorpus } from './manifest.mjs';
const corpus = JSON.parse(await readFile(new URL('./corpus.json', import.meta.url)));
validateCorpus(corpus);
for (const mutate of [
  value => { value.cases[0].revision = 'main'; },
  value => { value.cases[0].path = '../outside'; },
  value => { value.cases[0].calls = [{ tool: 'lsp_status', arguments: { operation: 'retry' } }]; },
  value => { value.cases[0].calls[1].assert = 'ignored-typo'; },
  value => { value.cases.push(value.cases[0]); },
]) {
  const invalid = structuredClone(corpus);
  mutate(invalid);
  assert.throws(() => validateCorpus(invalid));
}
console.log(`PASS ${corpus.cases.length} pinned scenarios; malformed and startup-only scenarios rejected`);
