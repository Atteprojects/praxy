// @ts-check
/** Every language this generator can emit. Adding one is a file here and an entry below. */

import { csharp } from "./csharp.mjs";
import { dart } from "./dart.mjs";

/** @type {Record<string, any>} */
export const targets = { dart, csharp };
