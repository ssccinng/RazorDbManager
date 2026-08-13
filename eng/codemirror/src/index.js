import { basicSetup } from "codemirror";
import { MySQL, sql } from "@codemirror/lang-sql";
import { HighlightStyle, syntaxHighlighting } from "@codemirror/language";
import { EditorState } from "@codemirror/state";
import { EditorView, placeholder } from "@codemirror/view";
import { tags } from "@lezer/highlight";

const editors = new WeakMap();
const sqlHighlightStyle = HighlightStyle.define([
  { tag: [tags.keyword, tags.operatorKeyword, tags.bool, tags.null], color: "var(--rdm-code-keyword)", fontWeight: "600" },
  { tag: [tags.string, tags.character], color: "var(--rdm-code-string)" },
  { tag: tags.number, color: "var(--rdm-code-number)" },
  { tag: tags.comment, color: "var(--rdm-code-comment)", fontStyle: "italic" },
  { tag: [tags.typeName, tags.className], color: "var(--rdm-code-type)" },
  { tag: tags.operator, color: "var(--rdm-code-operator)" },
  { tag: [tags.variableName, tags.propertyName], color: "var(--rdm-code-variable)" },
  { tag: [tags.punctuation, tags.separator], color: "var(--rdm-code-muted)" },
  { tag: tags.invalid, color: "var(--rdm-code-invalid)", textDecoration: "underline" }
]);

export function createSqlEditor(element, value, placeholderText, accessibilityLabel, callback) {
  const state = EditorState.create({
    doc: value ?? "",
    extensions: [
      basicSetup,
      sql({ dialect: MySQL }),
      syntaxHighlighting(sqlHighlightStyle),
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
