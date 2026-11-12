import loadPdfium from './vendor/pdfium.js';

const pdf = await loadPdfium({ locateFile: name => new URL(`./vendor/${name}`, import.meta.url).href });
pdf._FPDF_InitLibrary();

function documentRead(encoded, read) {
  const bytes = Uint8Array.from(atob(encoded), character => character.charCodeAt(0));
  const pointer = pdf._malloc(bytes.length);
  if (!pointer) throw new Error('The PDF is too large to load.');
  let document = 0;
  try {
    pdf.HEAPU8.set(bytes, pointer);
    document = pdf._FPDF_LoadMemDocument64(pointer, bytes.length, 0);
    if (!document) throw new Error(`PDFium could not load the document (error ${pdf._FPDF_GetLastError()}).`);
    return read(document);
  } finally {
    if (document) pdf._FPDF_CloseDocument(document);
    pdf._free(pointer);
  }
}

function pages(document, read) {
  return Array.from({ length: pdf._FPDF_GetPageCount(document) }, (_, index) => {
    const page = pdf._FPDF_LoadPage(document, index);
    if (!page) throw new Error(`Page ${index + 1} could not be read.`);
    try { return read(page); } finally { pdf._FPDF_ClosePage(page); }
  });
}

export function readPdf(encoded) {
  return JSON.stringify(documentRead(encoded, document => pages(document, page => {
    const Width = pdf._FPDF_GetPageWidthF(page), Height = pdf._FPDF_GetPageHeightF(page);
    const textPage = pdf._FPDFText_LoadPage(page);
    const scratch = pdf._malloc(40);
    let Text = '';
    const Boxes = [];
    try {
      const count = textPage ? pdf._FPDFText_CountChars(textPage) : 0;
      for (let index = 0; index < count; index++) {
        const boxIndex = index;
        let unicode = pdf._FPDFText_GetUnicode(textPage, index);
        if (unicode >= 0xd800 && unicode <= 0xdbff && index + 1 < count) {
          const low = pdf._FPDFText_GetUnicode(textPage, index + 1);
          if (low >= 0xdc00 && low <= 0xdfff) { unicode = 0x10000 + ((unicode - 0xd800) << 10) + low - 0xdc00; index++; }
        }
        if (!unicode || unicode > 0x10ffff || unicode >= 0xd800 && unicode <= 0xdfff) continue;
        let box = [0, 0, 0, 0];
        if (pdf._FPDFText_GetCharBox(textPage, boxIndex, scratch, scratch + 8, scratch + 16, scratch + 24)) {
          let heap = new DataView(pdf.HEAPU8.buffer);
          const left = heap.getFloat64(scratch, true), right = heap.getFloat64(scratch + 8, true);
          const bottom = heap.getFloat64(scratch + 16, true), top = heap.getFloat64(scratch + 24, true);
          const sizeX = Math.ceil(Width * 100), sizeY = Math.ceil(Height * 100);
          pdf._FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, left, bottom, scratch + 32, scratch + 36);
          heap = new DataView(pdf.HEAPU8.buffer);
          const x1 = heap.getInt32(scratch + 32, true), y1 = heap.getInt32(scratch + 36, true);
          pdf._FPDF_PageToDevice(page, 0, 0, sizeX, sizeY, 0, right, top, scratch + 32, scratch + 36);
          heap = new DataView(pdf.HEAPU8.buffer);
          const x2 = heap.getInt32(scratch + 32, true), y2 = heap.getInt32(scratch + 36, true);
          box = [Math.min(x1, x2) * Width / sizeX, Math.min(y1, y2) * Height / sizeY, Math.abs(x2 - x1) * Width / sizeX, Math.abs(y2 - y1) * Height / sizeY];
        }
        const character = String.fromCodePoint(unicode);
        Text += character;
        for (let unit = 0; unit < character.length; unit++) Boxes.push(box);
      }
    } finally { pdf._free(scratch); if (textPage) pdf._FPDFText_ClosePage(textPage); }
    return { Width, Height, Text, Boxes };
  })));
}

export function renderPdf(encoded, scale) {
  return JSON.stringify(documentRead(encoded, document => pages(document, page => {
    const Width = Math.max(1, Math.ceil(pdf._FPDF_GetPageWidthF(page) * scale));
    const Height = Math.max(1, Math.ceil(pdf._FPDF_GetPageHeightF(page) * scale));
    const bitmap = pdf._FPDFBitmap_Create(Width, Height, 1);
    if (!bitmap) throw new Error('The page is too large to draw at this zoom.');
    try {
      pdf._FPDFBitmap_FillRect(bitmap, 0, 0, Width, Height, 0xffffffff);
      pdf._FPDF_RenderPageBitmap(bitmap, page, 0, 0, Width, Height, 0, 1);
      const Stride = pdf._FPDFBitmap_GetStride(bitmap);
      const pointer = pdf._FPDFBitmap_GetBuffer(bitmap);
      const bytes = pdf.HEAPU8.subarray(pointer, pointer + Stride * Height);
      let binary = '';
      for (let index = 0; index < bytes.length; index += 0x8000) binary += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
      return { Pixels: btoa(binary), Width, Height, Stride };
    } finally { pdf._FPDFBitmap_Destroy(bitmap); }
  })));
}
