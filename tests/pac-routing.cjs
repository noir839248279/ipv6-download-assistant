const fs = require("node:fs");
const vm = require("node:vm");
const assert = require("node:assert/strict");

let count = 0;
for (const { pac, cases } of JSON.parse(fs.readFileSync(process.argv[2], "utf8"))) {
  const context = vm.createContext({
    isPlainHostName: host => !host.includes(".") && !host.includes(":"),
    dnsDomainIs: (host, suffix) => host.endsWith(suffix),
    shExpMatch: (text, glob) => new RegExp("^" + glob.split("*")
      .map(part => part.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join(".*") + "$").test(text),
  });
  vm.runInContext(pac, context, { timeout: 1000 });
  for (const { url, expected } of cases) {
    assert.equal(context.FindProxyForURL(url, new URL(url).hostname), expected, url);
    count++;
  }
}
console.log(`PASS: ${count} executed PAC routing cases`);
