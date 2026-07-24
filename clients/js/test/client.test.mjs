import { test } from "node:test";
import assert from "node:assert/strict";
import { LogrrClient, LogrrLevel } from "../dist/index.js";

/** A fake fetch that records requests and returns a configurable response. */
function fakeFetch(responder = () => new Response(null, { status: 201 })) {
  const calls = [];
  const fn = async (url, init) => {
    calls.push({ url, init, body: init.body });
    return responder(url, init);
  };
  fn.calls = calls;
  return fn;
}

const base = { endpoint: "https://logrr.internal/", apiKey: "lg_test_key" };

test("flush ships buffered events as CLEF with the api-key header", async () => {
  const fetch = fakeFetch();
  const client = new LogrrClient({ ...base, fetch });
  client.info("User {UserId} signed in", { UserId: 7 });
  await client.flush();

  assert.equal(fetch.calls.length, 1);
  const call = fetch.calls[0];
  assert.equal(call.url, "https://logrr.internal/api/events/raw");
  assert.equal(call.init.headers["X-Logrr-ApiKey"], "lg_test_key");
  assert.equal(call.init.headers["Content-Type"], "application/vnd.serilog.clef");
  const obj = JSON.parse(call.body);
  assert.equal(obj["@mt"], "User {UserId} signed in");
  assert.equal(obj.UserId, 7);
  await client.close();
});

test("reaching the batch size limit triggers an automatic flush", async () => {
  const fetch = fakeFetch();
  const client = new LogrrClient({ ...base, fetch, batchSizeLimit: 3, flushIntervalMs: 60_000 });
  client.info("a");
  client.info("b");
  assert.equal(fetch.calls.length, 0); // under the limit, nothing sent yet
  client.info("c"); // hits the limit → auto flush
  await client.flush();
  assert.equal(fetch.calls.length, 1);
  assert.equal(fetch.calls[0].body.split("\n").length, 3);
  await client.close();
});

test("events below minimumLevel are dropped client-side", async () => {
  const fetch = fakeFetch();
  const client = new LogrrClient({ ...base, fetch, minimumLevel: LogrrLevel.Warning });
  client.info("ignored");
  client.debug("ignored");
  client.warn("kept");
  await client.flush();
  assert.equal(fetch.calls.length, 1);
  assert.equal(fetch.calls[0].body.split("\n").length, 1);
  await client.close();
});

test("queueLimit sheds events and counts them as dropped", async () => {
  const fetch = fakeFetch();
  const client = new LogrrClient({ ...base, fetch, queueLimit: 2, flushIntervalMs: 60_000 });
  client.info("1");
  client.info("2");
  client.info("3"); // over the cap → dropped, not buffered
  assert.equal(client.droppedCount, 1);
  await client.close();
});

test("a non-2xx response drops the batch and invokes onError", async () => {
  const errors = [];
  const fetch = fakeFetch(() => new Response("nope", { status: 500 }));
  const client = new LogrrClient({
    ...base,
    fetch,
    onError: (err, n) => errors.push({ err, n }),
  });
  client.error("boom");
  await client.flush();
  assert.equal(client.droppedCount, 1);
  assert.equal(errors.length, 1);
  assert.equal(errors[0].n, 1);
  await client.close();
});

test("a transport failure drops the batch without throwing", async () => {
  const fetch = () => Promise.reject(new Error("ECONNREFUSED"));
  const client = new LogrrClient({ ...base, fetch });
  client.info("x");
  await client.flush(); // must not reject
  assert.equal(client.droppedCount, 1);
  await client.close();
});

test("close() performs a final flush", async () => {
  const fetch = fakeFetch();
  const client = new LogrrClient({ ...base, fetch, flushIntervalMs: 60_000 });
  client.info("last");
  await client.close();
  assert.equal(fetch.calls.length, 1);
});

test("missing endpoint or apiKey throws at construction", () => {
  assert.throws(() => new LogrrClient({ endpoint: "", apiKey: "k" }));
  assert.throws(() => new LogrrClient({ endpoint: "https://x", apiKey: "" }));
});
