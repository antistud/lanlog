import { test } from "node:test";
import assert from "node:assert/strict";
import { serializeClef, LogrrLevel } from "../dist/index.js";

test("serialises reserved fields and level name", () => {
  const line = serializeClef([
    {
      timestamp: new Date("2026-07-23T14:02:11.427Z"),
      level: LogrrLevel.Error,
      messageTemplate: "Payment {Amount} failed for {UserId}",
      exception: "System.TimeoutException: ...",
      source: "Billing.Processor",
      properties: { Amount: 49.99, UserId: 1042 },
    },
  ]);
  const obj = JSON.parse(line);
  assert.equal(obj["@t"], "2026-07-23T14:02:11.427Z");
  assert.equal(obj["@mt"], "Payment {Amount} failed for {UserId}");
  assert.equal(obj["@l"], "Error");
  assert.equal(obj["@x"], "System.TimeoutException: ...");
  assert.equal(obj["SourceContext"], "Billing.Processor");
  assert.equal(obj.Amount, 49.99);
  assert.equal(obj.UserId, 1042);
});

test("absent level defaults to Information", () => {
  const obj = JSON.parse(serializeClef([{ messageTemplate: "hi" }]));
  assert.equal(obj["@l"], "Information");
});

test("a non-reserved @-prefixed property is escaped by doubling the @", () => {
  // The server unescapes "@@custom" back to a literal "@custom" property (SPEC §6.1).
  const obj = JSON.parse(
    serializeClef([{ messageTemplate: "x", properties: { "@custom": "literal" } }]),
  );
  assert.equal(obj["@@custom"], "literal");
});

test("a property colliding with a reserved field (@x) is dropped, matching the .NET client", () => {
  const obj = JSON.parse(
    serializeClef([{ messageTemplate: "x", properties: { "@x": "spoof" } }]),
  );
  assert.ok(!("@x" in obj) && !("@@x" in obj));
});

test("properties colliding with reserved fields are dropped", () => {
  const obj = JSON.parse(
    serializeClef([
      { messageTemplate: "x", source: "real", properties: { SourceContext: "spoof", "@l": "spoof" } },
    ]),
  );
  assert.equal(obj["SourceContext"], "real");
  assert.equal(obj["@l"], "Information");
});

test("undefined property values are skipped", () => {
  const obj = JSON.parse(
    serializeClef([{ messageTemplate: "x", properties: { Kept: 1, Skipped: undefined } }]),
  );
  assert.ok("Kept" in obj);
  assert.ok(!("Skipped" in obj));
});

test("an Error exception captures its stack/message", () => {
  const obj = JSON.parse(
    serializeClef([{ messageTemplate: "x", exception: new Error("boom") }]),
  );
  assert.match(obj["@x"], /boom/);
});

test("newline-delimited: N events → N lines", () => {
  const payload = serializeClef([{ messageTemplate: "a" }, { messageTemplate: "b" }]);
  assert.equal(payload.split("\n").length, 2);
});
