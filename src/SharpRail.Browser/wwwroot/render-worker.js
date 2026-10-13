const ready = Promise.all([import('./vendor/merman_wasm.js'), import('./pdf.js')]).then(async ([mermaid, pdf]) => {
  await mermaid.default();
  return { mermaid, pdf };
});
onmessage = async ({ data: { id, kind, args } }) => {
  try {
    const { mermaid, pdf } = await ready;
    const result = kind === 'mermaid' ? mermaid.renderSvg(...args)
      : kind === 'pdf-read' ? pdf.readPdf(...args) : pdf.renderPdf(...args);
    postMessage({ id, result });
  } catch (error) { postMessage({ id, error: error.message ?? String(error) }); }
};
