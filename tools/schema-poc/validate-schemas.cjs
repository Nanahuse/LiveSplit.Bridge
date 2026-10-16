const fs = require("node:fs");
const path = require("node:path");
const Ajv2020 = require("ajv/dist/2020").default;

const outputDir = path.resolve(process.argv[2] || path.join(__dirname, "out"));
const cases = JSON.parse(
  fs.readFileSync(path.join(__dirname, "samples", "validation-cases.json"), "utf8"),
);
const ajv = new Ajv2020({ allErrors: true, strict: true });
const validators = {
  request: ajv.compile(JSON.parse(fs.readFileSync(path.join(outputDir, "rpc-request.schema.json"), "utf8"))),
  response: ajv.compile(JSON.parse(fs.readFileSync(path.join(outputDir, "rpc-response.schema.json"), "utf8"))),
};

let failures = 0;
for (const sample of cases) {
  const accepted = validators[sample.schema](sample.data);
  const passed = accepted === sample.valid;
  console.log(`${passed ? "PASS" : "FAIL"} ${sample.schema}: ${sample.name}`);
  if (!passed) {
    failures += 1;
    console.error(validators[sample.schema].errors);
  }
}

console.log(`Ajv Draft 2020-12: ${cases.length - failures}/${cases.length} cases passed`);
if (failures > 0) process.exitCode = 1;
