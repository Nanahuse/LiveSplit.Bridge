const fs = require("node:fs");
const path = require("node:path");
const Ajv2020 = require("ajv/dist/2020").default;

const root = __dirname;
const outputDir = path.resolve(process.argv[2] || path.join(root, "out"));
const schemaFiles = {
  request: "rpc-request.schema.json",
  response: "rpc-response.schema.json",
  event: "bridge-event.schema.json",
};
const ajv = new Ajv2020({ allErrors: true, strict: true, validateFormats: false });
const validators = {};

for (const [kind, filename] of Object.entries(schemaFiles)) {
  const schema = JSON.parse(fs.readFileSync(path.join(outputDir, filename), "utf8"));
  validators[kind] = ajv.compile(schema);
}

const fixtures = JSON.parse(fs.readFileSync(path.join(root, "samples", "validation-cases.json"), "utf8"));
const serialized = JSON.parse(fs.readFileSync(path.join(outputDir, "csharp-serialized-cases.json"), "utf8"));
let failures = 0;
let fixturePasses = 0;
let serializedPasses = 0;

function check(test, source) {
  const valid = validators[test.schema](test.data);
  const passed = valid === test.valid;
  console.log(`${passed ? "PASS" : "FAIL"} ${source}/${test.schema}: ${test.name}`);
  if (passed && source === "fixture") fixturePasses += 1;
  if (passed && source === "csharp") serializedPasses += 1;
  if (!passed) {
    failures += 1;
    console.error(validators[test.schema].errors);
  }
}

for (const test of fixtures) check(test, "fixture");
for (const test of serialized) check(test, "csharp");

console.log(`Draft 2020-12 schema documents compiled: ${Object.keys(validators).length}/3`);
console.log(`Protocol constraint fixtures: ${fixturePasses}/${fixtures.length} passed`);
console.log(`Actual C# serialized messages: ${serializedPasses}/${serialized.length} passed`);
if (failures > 0) process.exitCode = 1;
