"use client";

import { useState } from "react";

type Props = {
  bodyText: string;
  bodyJson: unknown;
};

export default function BodyViewer({ bodyText, bodyJson }: Props) {
  const [mode, setMode] = useState<"raw" | "pretty">(bodyJson !== null ? "pretty" : "raw");
  const hasJson = bodyJson !== null;

  const text =
    mode === "pretty" && hasJson
      ? JSON.stringify(bodyJson, null, 2)
      : bodyText;

  return (
    <>
      {hasJson && (
        <div className="viewer-toolbar">
          <button
            type="button"
            className={`btn ${mode === "raw" ? "btn-primary" : ""}`}
            onClick={() => setMode("raw")}
          >
            Raw
          </button>
          <button
            type="button"
            className={`btn ${mode === "pretty" ? "btn-primary" : ""}`}
            onClick={() => setMode("pretty")}
          >
            Pretty JSON
          </button>
        </div>
      )}
      <pre>{text || "(empty body)"}</pre>
    </>
  );
}
