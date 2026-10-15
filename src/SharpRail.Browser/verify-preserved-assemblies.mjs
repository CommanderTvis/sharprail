import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';

const entries = (await fs.readFile(process.argv[2], 'utf8')).trim().split(/\r?\n/).filter(Boolean);
assert.ok(entries.length > 0, 'The browser copy-only publication must preserve managed assemblies.');
for (const entry of entries) {
  const [original, published] = entry.split('|');
  assert.ok(original && published, 'The preserved-assembly manifest must name both files.');
  const [before, after] = await Promise.all([fs.readFile(original), fs.readFile(published)]);
  const hash = bytes => createHash('sha256').update(bytes).digest('hex');
  assert.equal(hash(after), hash(before), 'Browser publication changed managed IL: ' + original);
}
console.log('Verified byte-identical preservation of ' + entries.length + ' browser managed assemblies.');
