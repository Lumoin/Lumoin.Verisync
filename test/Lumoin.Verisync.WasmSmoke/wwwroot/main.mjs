// The runner avoids DOM access so the same bundle runs under Node in CI and in a browser tab.
// Main's failure count becomes the process exit code under Node.
import { dotnet } from './_framework/dotnet.js'

const runtime = await dotnet.create();
const exitCode = await runtime.runMainAndExit();
console.log(`wasm smoke exit code: ${exitCode}`);
