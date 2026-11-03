// just-bash's gzip, gunzip and zcat use node:zlib, which a browser does not have; every other command works without it.
const unavailable = () => { throw new Error('gzip is not available in the Playground'); };
export const constants = {};
export const gunzipSync = unavailable;
export const gzipSync = unavailable;
