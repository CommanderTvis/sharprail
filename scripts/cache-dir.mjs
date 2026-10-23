// The machine-wide build cache every checkout shares: SHARPRAIL_CACHE_DIR from mise.toml, else the same default, for
// scripts that run outside mise. Nothing is cached in the checkout. A root created here is excluded from Time Machine on
// macOS, as scripts/native-cache.sh does.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

export function cacheDir(...parts) {
  const root = process.env.SHARPRAIL_CACHE_DIR || (process.platform === 'win32'
    ? path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), 'AppData', 'Local'), 'sharprail', 'cache')
    : path.join(process.env.XDG_CACHE_HOME || path.join(os.homedir(), '.cache'), 'sharprail'));
  if (!fs.existsSync(root)) {
    fs.mkdirSync(root, { recursive: true });
    if (process.platform === 'darwin') spawnSync('tmutil', ['addexclusion', root], { stdio: 'ignore' });
  }
  return path.join(root, ...parts);
}
