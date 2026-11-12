// The CSS language server for the Playground: vscode-css-languageservice behind the LSP, run in a Web Worker. The page
// posts JSON-RPC messages to it and reads the server's back; no process or file system is involved, only open documents.
import { createConnection, BrowserMessageReader, BrowserMessageWriter, TextDocuments, TextDocumentSyncKind } from 'vscode-languageserver/browser';
import { TextDocument } from 'vscode-languageserver-textdocument';
import { getCSSLanguageService, getSCSSLanguageService, getLESSLanguageService } from 'vscode-css-languageservice';

// The page asks for the worker's usage out of band, before the connection sees the message: the time it spent handling
// messages (a turn of the event loop each, so an upper bound) and its JS heap where the browser reports one.
let busy = 0;
self.addEventListener('message', event => {
  if (event.data?.sharprail === 'usage') {
    event.stopImmediatePropagation();
    self.postMessage({ sharprail: 'usage', busyMs: busy, heapBytes: performance.memory?.usedJSHeapSize ?? 0 });
    return;
  }
  const start = performance.now();
  setTimeout(() => { busy += performance.now() - start; }, 0);
});

const connection = createConnection(new BrowserMessageReader(self), new BrowserMessageWriter(self));
const documents = new TextDocuments(TextDocument);
const services = { css: getCSSLanguageService(), scss: getSCSSLanguageService(), less: getLESSLanguageService() };
const serviceOf = document => services[document.languageId] ?? services.css;
const stylesheets = new Map();
const stylesheetOf = document => {
  const cached = stylesheets.get(document.uri);
  if (cached?.version === document.version) return cached.value;
  const value = serviceOf(document).parseStylesheet(document);
  stylesheets.set(document.uri, { version: document.version, value });
  return value;
};

const languageOf = uri => uri.endsWith('.scss') ? 'scss' : uri.endsWith('.less') ? 'less' : 'css';

connection.onInitialize(() => ({
  capabilities: {
    textDocumentSync: TextDocumentSyncKind.Incremental,
    completionProvider: { triggerCharacters: ['/', '-', ':'], resolveProvider: false },
    hoverProvider: true,
    definitionProvider: true,
    referencesProvider: true,
    documentSymbolProvider: true,
    documentHighlightProvider: true,
    colorProvider: true,
    renameProvider: true
  }
}));

const validate = document => {
  const diagnostics = serviceOf(document).doValidation(document, stylesheetOf(document));
  connection.sendDiagnostics({ uri: document.uri, version: document.version, diagnostics });
};

documents.onDidChangeContent(event => validate(event.document));
documents.onDidClose(event => { stylesheets.delete(event.document.uri); connection.sendDiagnostics({ uri: event.document.uri, diagnostics: [] }); });

// The client names no language for a file it opened by extension alone, so the id is read off the uri.
const open = uri => {
  const document = documents.get(uri);
  return document && document.languageId === languageOf(uri) ? document : document && TextDocument.create(uri, languageOf(uri), document.version, document.getText());
};

connection.onCompletion(({ textDocument, position }) => {
  const document = open(textDocument.uri);
  return document ? serviceOf(document).doComplete(document, position, stylesheetOf(document)) : null;
});
connection.onHover(({ textDocument, position }) => {
  const document = open(textDocument.uri);
  return document ? serviceOf(document).doHover(document, position, stylesheetOf(document)) : null;
});
connection.onDefinition(({ textDocument, position }) => {
  const document = open(textDocument.uri);
  return document ? serviceOf(document).findDefinition(document, position, stylesheetOf(document)) : null;
});
connection.onReferences(({ textDocument, position }) => {
  const document = open(textDocument.uri);
  return document ? serviceOf(document).findReferences(document, position, stylesheetOf(document)) : [];
});
connection.onDocumentSymbol(({ textDocument }) => {
  const document = open(textDocument.uri);
  return document ? serviceOf(document).findDocumentSymbols2(document, stylesheetOf(document)) : [];
});
connection.onDocumentHighlight(({ textDocument, position }) => {
  const document = open(textDocument.uri);
  return document ? serviceOf(document).findDocumentHighlights(document, position, stylesheetOf(document)) : [];
});
connection.onDocumentColor(({ textDocument }) => {
  const document = open(textDocument.uri);
  return document ? serviceOf(document).findDocumentColors(document, stylesheetOf(document)) : [];
});
connection.onRenameRequest(({ textDocument, position, newName }) => {
  const document = open(textDocument.uri);
  return document ? serviceOf(document).doRename(document, position, newName, stylesheetOf(document)) : null;
});

documents.listen(connection);
connection.listen();
