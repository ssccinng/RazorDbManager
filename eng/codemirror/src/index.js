import { basicSetup } from "codemirror";
import { MySQL, sql } from "@codemirror/lang-sql";
import { EditorState } from "@codemirror/state";
import { EditorView, placeholder } from "@codemirror/view";

const editors = new WeakMap();

export function createSqlEditor(element, value, placeholderText, accessibilityLabel, callback) {
  const state = EditorState.create({
    doc: value ?? "",
    extensions: [
      basicSetup,
      sql({ dialect: MySQL }),
      EditorView.lineWrapping,
      placeholder(placeholderText ?? ""),
      EditorView.contentAttributes.of({
        "aria-label": accessibilityLabel ?? "SQL editor",
        "spellcheck": "false"
      }),
      EditorView.updateListener.of(update => {
        if (update.docChanged) {
          void callback.invokeMethodAsync("OnEditorValueChanged", update.state.doc.toString());
        }
      })
    ]
  });

  const view = new EditorView({ state, parent: element });
  editors.set(element, view);
}

export function setSqlEditorValue(element, value) {
  const view = editors.get(element);
  if (!view) return;
  const next = value ?? "";
  const current = view.state.doc.toString();
  if (current === next) return;
  view.dispatch({ changes: { from: 0, to: current.length, insert: next } });
}

export function focusSqlEditor(element) {
  editors.get(element)?.focus();
}

export function destroySqlEditor(element) {
  const view = editors.get(element);
  if (!view) return;
  view.destroy();
  editors.delete(element);
}

export function downloadTextFile(fileName, content, contentType) {
  const blob = new Blob([content ?? ""], { type: contentType ?? "text/plain;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = fileName;
  anchor.style.display = "none";
  try {
    document.body.appendChild(anchor);
    anchor.click();
  } finally {
    anchor.remove();
    // Some browsers consume the object URL asynchronously after click returns.
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}
