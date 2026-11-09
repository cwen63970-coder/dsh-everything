#!/usr/bin/env node
// MCP stdio server exposing the Everything (voidtools) search index to DSH.
//
// Why a custom bridge instead of an off-the-shelf Everything MCP server: the
// official Everything SDK is a thin wrapper over the WM_COPYDATA IPC protocol,
// and the protocol is small enough to speak directly. This server therefore has
// no npm dependencies at all -- it drives everything-ipc.exe, a tiny .NET helper
// compiled from everything-ipc.cs in this same directory.
//
// Everything's own command line is NOT usable for search: it has no
// "search and print results" switch (verified against `Everything.exe -help`),
// which is exactly why the IPC route is required.
//
// Modes:
//   node everything-mcp.mjs         -> MCP stdio server
//   node everything-mcp.mjs status  -> human-readable health check
//   node everything-mcp.mjs search <query> [options]   (see the usage text below)

import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const BRIDGE = join(HERE, "everything-ipc.exe");
const SERVER_NAME = "everything";
const SERVER_VERSION = "1.0.0";

const SORTS = [
  "name_asc", "name_desc",
  "path_asc", "path_desc",
  "size_asc", "size_desc",
  "ext_asc", "ext_desc",
  "date_modified_asc", "date_modified_desc",
  "date_created_asc", "date_created_desc",
  "date_accessed_asc", "date_accessed_desc",
  "runcount_asc", "runcount_desc",
];

function log(...args) {
  process.stderr.write(`[everything] ${args.join(" ")}\n`);
}

// --------------------------------------------------------------- IPC bridge

/**
 * Long-lived child running everything-ipc.exe, spoken to over line-delimited
 * JSON. Started once and reused: spawning per call would add a process start to
 * every query, and Everything queries are meant to be interactive.
 */
class Bridge {
  constructor() {
    this.child = null;
    this.buffer = "";
    this.pending = [];
    this.starting = null;
    this.queue = Promise.resolve();
  }

  async start() {
    if (this.child && !this.child.killed) return;
    if (this.starting) return this.starting;

    this.starting = new Promise((resolve, reject) => {
      if (process.platform !== "win32") {
        reject(new Error(
          `Everything (voidtools) only runs on Windows, so this MCP server has nothing to query ` +
          `on ${process.platform}. Disable the dsh-everything plugin on this machine.`,
        ));
        return;
      }
      if (!existsSync(BRIDGE)) {
        reject(new Error(
          `IPC bridge not found at ${BRIDGE}. Compile it with:\n` +
          `  csc.exe /nologo /target:exe /platform:anycpu /optimize+ ` +
          `/reference:System.Windows.Forms.dll /reference:System.Drawing.dll ` +
          `/out:everything-ipc.exe everything-ipc.cs`,
        ));
        return;
      }

      const child = spawn(BRIDGE, [], { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
      this.child = child;
      this.buffer = "";

      child.stdout.setEncoding("utf8");
      child.stdout.on("data", (chunk) => this._onData(chunk));
      child.stderr.setEncoding("utf8");
      child.stderr.on("data", (chunk) => {
        const text = chunk.trim();
        if (text) log(text);
      });
      child.on("error", (error) => {
        this.child = null;
        this.starting = null;
        this._failAll(error);
        reject(error);
      });
      child.on("exit", (code) => {
        this.child = null;
        this.starting = null;
        this._failAll(new Error(`IPC bridge exited with code ${code}`));
      });

      resolve();
    });

    return this.starting;
  }

  _onData(chunk) {
    this.buffer += chunk;
    let index;
    while ((index = this.buffer.indexOf("\n")) >= 0) {
      const line = this.buffer.slice(0, index).trim();
      this.buffer = this.buffer.slice(index + 1);
      if (!line) continue;
      const waiter = this.pending.shift();
      if (!waiter) {
        log(`unexpected bridge output: ${line.slice(0, 200)}`);
        continue;
      }
      try {
        waiter.resolve(JSON.parse(line));
      } catch (error) {
        waiter.reject(new Error(`invalid bridge response: ${line.slice(0, 200)}`));
      }
    }
  }

  _failAll(error) {
    const waiters = this.pending;
    this.pending = [];
    for (const waiter of waiters) waiter.reject(error);
  }

  /**
   * Queue one command behind the previous one, then run it.
   *
   * The bridge answers with one unterminated-by-id JSON line per request, so
   * replies are matched by order alone: two commands in flight could swap
   * results. A timeout also has to drop the child to resync that stream, which
   * under concurrency would take unrelated queries down with it. Serialising
   * keeps both failure modes local to the request that caused them.
   */
  request(command, timeoutMs = 60000) {
    const run = () => this._requestOnce(command, timeoutMs);
    this.queue = this.queue.then(run, run);
    return this.queue;
  }

  /** Send one command and await its single-line JSON reply. */
  async _requestOnce(command, timeoutMs = 60000) {
    await this.start();

    return new Promise((resolve, reject) => {
      const waiter = { resolve: null, reject: null };
      const timer = setTimeout(() => {
        const index = this.pending.indexOf(waiter);
        if (index >= 0) this.pending.splice(index, 1);
        // A wedged bridge is useless; drop it so the next call respawns.
        try { this.child?.kill(); } catch { /* already gone */ }
        reject(new Error(`Everything query timed out after ${timeoutMs} ms`));
      }, timeoutMs);

      waiter.resolve = (value) => { clearTimeout(timer); resolve(value); };
      waiter.reject = (error) => { clearTimeout(timer); reject(error); };
      this.pending.push(waiter);

      try {
        this.child.stdin.write(`${JSON.stringify(command)}\n`);
      } catch (error) {
        clearTimeout(timer);
        const index = this.pending.indexOf(waiter);
        if (index >= 0) this.pending.splice(index, 1);
        reject(error);
      }
    });
  }

  stop() {
    if (this.child) {
      const child = this.child;
      this.child = null;
      try { child.stdin.end(); } catch { /* ignore */ }
      try { child.kill(); } catch { /* ignore */ }
    }
  }
}

const bridge = new Bridge();

// ------------------------------------------------------------- presentation

/**
 * Bound on how many characters a single tool result may return. A query can match
 * tens of thousands of rows, and an unbounded reply would be truncated by the
 * transport instead of by us -- losing the summary and the truncation notice.
 *
 * 40k characters is roughly 10-15k tokens: large enough for a full page of paths,
 * small enough that one call cannot eat a quarter of the model's context.
 */
const MAX_RESULT_CHARS = 40000;

function clampText(text) {
  if (text.length <= MAX_RESULT_CHARS) return text;
  // Cut on a line boundary: half a path at the tail reads like a real path, and
  // a model will happily report the fragment as a file that exists.
  let cut = text.lastIndexOf("\n", MAX_RESULT_CHARS);
  if (cut <= 0) cut = MAX_RESULT_CHARS;
  const kept = text.slice(0, cut);
  const droppedChars = text.length - kept.length;
  const droppedLines = text.split("\n").length - kept.split("\n").length;
  return `${kept}\n\n[output truncated: ${droppedChars} more characters ` +
    `(~${droppedLines} lines) were dropped. Use everything_count for a total, ` +
    `narrow the query, page with offset, or lower max.]`;
}

function formatSize(bytes) {
  if (bytes === null || bytes === undefined) return "";
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB", "TB", "PB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[unit]}`;
}

/**
 * One compact block per hit. The full path is the identity the model needs, so
 * it leads; everything else is appended only when known.
 */
function formatHit(item, index) {
  const kind = item.isFolder ? "DIR " : "FILE";
  const facts = [];
  if (!item.isFolder && item.size !== null && item.size !== undefined) {
    facts.push(formatSize(item.size));
  }
  if (item.dateModified) facts.push(`mod ${item.dateModified.slice(0, 16).replace("T", " ")}`);
  const suffix = facts.length > 0 ? `  (${facts.join(", ")})` : "";
  return `${String(index + 1).padStart(3)}. [${kind}] ${item.fullPath}${suffix}`;
}

/**
 * Render one page, shrinking it until it fits the tool budget.
 *
 * Rows are dropped from the *end* and every count is recomputed from the rows that
 * actually survive, so the "showing N of M" line, the visible rows and the next
 * `offset` always agree. Cutting characters afterwards would print a page size the
 * text does not contain -- and an offset that skips rows the caller never saw.
 */
function summarize(payload, heading, warning) {
  const items = Array.isArray(payload.results) ? payload.results : [];
  const offset = payload.offset ?? 0;

  const render = (rows) => {
    const lines = [];

    // Anything that makes the list untrustworthy goes above the list, where a model
    // skimming the result cannot miss it: first a stale-index warning, then the
    // truncation notice.
    if (warning) lines.push(warning, "");

    const shownTo = offset + rows.length;
    const truncated = payload.total > shownTo;
    const outOfRange = offset > 0 && offset >= payload.total;
    if (truncated) {
      const remaining = payload.total - shownTo;
      lines.push(
        `INCOMPLETE RESULT SET — this is NOT the complete list.`,
        `${payload.total} items match; showing ${rows.length} ` +
        `(${shownTo} of ${payload.total}), ${remaining} not shown.`,
        `To get the complete set: page with offset=${shownTo}, or narrow the query so it matches ` +
        `${rows.length} or fewer items. Do not report this list as complete.`,
        "",
      );
    }

    lines.push(`${heading}: ${payload.total} match${payload.total === 1 ? "" : "es"}`);

    if (outOfRange) {
      // Otherwise this reads exactly like "the query matched nothing", which is a
      // different claim from "you paged past the end".
      lines.push(
        payload.total === 0
          ? `offset=${offset} cannot be used: the query matched nothing.`
          : `offset=${offset} is past the end: only ${payload.total} ` +
            `item${payload.total === 1 ? "" : "s"} match, so valid offsets are 0-${payload.total - 1}.`,
      );
    } else if (rows.length === 0) {
      lines.push("(no rows transferred)");
    } else {
      rows.forEach((item, index) => lines.push(formatHit(item, index)));
    }

    if (truncated) {
      lines.push("", `... ${payload.total - shownTo} further matches are NOT listed above; ` +
        `use offset=${shownTo} to continue.`);
    }
    return lines;
  };

  // Largest prefix of the page that still fits the budget. Rendering grows with the
  // row count, so a binary search lands on the exact cut in ~log2(300) renders
  // instead of stepping down by fractions and wasting part of the budget.
  let low = 0;
  let high = items.length;
  while (low < high) {
    const mid = Math.ceil((low + high) / 2);
    if (render(items.slice(0, mid)).join("\n").length <= MAX_RESULT_CHARS) low = mid;
    else high = mid - 1;
  }
  return render(items.slice(0, low)).join("\n");
}

// ------------------------------------------------------------------- tools

const TOOLS = [
  {
    name: "everything_search",
    description:
      "PRIMARY TOOL for locating files and folders by name, extension, path, size, or date.\n" +
      "Reach for this FIRST whenever the user wants to find where a file is, when you do not " +
      "already know an exact path, or when the target may lie outside the working directory. " +
      "It queries a prebuilt index of the volumes Everything has indexed, so a name lookup comes " +
      "back immediately and needs no directory walking.\n\n" +
      "Returns paths, sizes and timestamps only — never file contents; use read/grep for those. " +
      "An empty result means 'not in the current index', not 'does not exist': volumes Everything " +
      "does not index, and an in-progress rebuild, both cause misses (everything_status says so).\n\n" +
      "Use `glob` instead when you must have an exact, complete, workspace-relative enumeration " +
      "(glob never truncates; it spills an oversized list to a file and gives you the path). " +
      "everything_search returns a top-N page and says so loudly when it is incomplete.\n\n" +
      "The query uses Everything's native search syntax:\n" +
      "  *.log                  wildcards on the name\n" +
      "  ext:rs;ts              multiple extensions\n" +
      "  D:\\projects *.ts       a path term plus a name term (space = AND)\n" +
      "  size:>100mb            size comparisons (kb/mb/gb)\n" +
      "  dm:today               date-modified (today, thisweek, last7days, 2026-01-01..2026-06-30)\n" +
      "  \"exact phrase\"         quoted literal\n" +
      "  file: / folder:        restrict to files or folders (built-in modifiers, always work)\n" +
      "  c:|d:                   either drive;  !e:  excludes a drive\n" +
      "  startwith:README  endwith:.md   anchor the name\n" +
      "  len:>100               name length in UTF-16 characters\n" +
      "  parent:D:\\repo\\src     direct children of one folder, not recursive\n" +
      "  dupe:                  names that occur more than once\n" +
      "  empty:                 empty folders;   root:  items with no parent\n" +
      "Combine terms freely; every term must match.\n\n" +
      "One trap in that list: date offsets always need the last/past/prev/next prefix. " +
      "`dm:last7days` means the last 7 days; a bare `dm:7days` is not a relative range and " +
      "silently matches unrelated old files instead of failing.\n\n" +
      "DO NOT use type macros like `exe:`, `doc:`, `audio:`, `video:`, `pic:` or `zip:`. " +
      "They are user-defined filters, not built-ins, and on this machine they are undefined, so " +
      "they silently return ZERO results without any error. Use `ext:exe`, `ext:doc;pdf;txt` or " +
      "`*.exe` instead. (`type:video` and friends depend on optional media properties that are " +
      "also not indexed here.)\n\n" +
      "MATCHING AGAINST THE PATH — a leading backslash is the portable way to do it, and it is the " +
      "single most useful thing to know here:\n" +
      "  \\node_modules          matches the full path, so it finds every item INSIDE that folder\n" +
      "  node_modules           matches the NAME only (files actually named that)\n" +
      "The difference is not cosmetic: without the backslash you match only entries whose NAME is " +
      "exactly that (a handful at most), while with it you match everything living under every " +
      "folder of that name — which is how a dependency tree ends up being counted.\n\n" +
      "Excluding directories — the syntax matters, and the obvious guess silently does nothing:\n" +
      "  !\\node_modules         CORRECT: excludes that directory subtree\n" +
      "  !\\src\\                 CORRECT: trailing backslash works too\n" +
      "  !path:node_modules     works, but `path:` is an Everything 1.5 modifier\n" +
      "  !folder:node_modules   WRONG: silently matches nothing, results stay unfiltered\n" +
      "  !node_modules          WRONG: same silent no-op\n" +
      "Nothing is excluded by default, so add `!\\` terms yourself when a query would otherwise be " +
      "dominated by dependencies or build output. Multiple exclusions combine:\n" +
      "  D:\\repo *.ts !\\node_modules !\\.git !\\dist",
    inputSchema: {
      type: "object",
      properties: {
        query: {
          type: "string",
          description: "Everything search syntax. Empty string lists everything (use with a small max).",
        },
        max: {
          type: "integer",
          description: "Rows to return (default 50, max 300). The true hit count is always reported separately; " +
            "use everything_count when you only need the number.",
          minimum: 1,
          maximum: 300,
        },
        offset: {
          type: "integer",
          description: "Skip this many matches, for paging through a large result set (default 0).",
          minimum: 0,
        },
        sort: {
          type: "string",
          enum: SORTS,
          description: "Result order. Default name_asc, which Everything can always serve without extra indexing.",
        },
        foldersOnly: { type: "boolean", description: "Return only folders." },
        filesOnly: { type: "boolean", description: "Return only files." },
        matchPath: { type: "boolean", description: "Match the query against the full path instead of just the file name." },
        matchCase: { type: "boolean", description: "Case-sensitive matching." },
        wholeWord: { type: "boolean", description: "Whole-word matching." },
        regex: { type: "boolean", description: "Treat the query as a regular expression (Everything's own regex dialect)." },
      },
      required: ["query"],
      additionalProperties: false,
    },
  },
  {
    name: "everything_count",
    description:
      "Count how many files and folders match an Everything query without transferring any rows. " +
      "Use this to gauge scale before paging, or to answer 'how many X are on this disk'. " +
      "Prefer this over everything_search when you only need the number, and check it before " +
      "treating a search result as the complete set.",
    inputSchema: {
      type: "object",
      properties: {
        query: { type: "string", description: "Everything search syntax." },
        matchPath: { type: "boolean", description: "Match against the full path." },
        matchCase: { type: "boolean", description: "Case-sensitive matching." },
        regex: { type: "boolean", description: "Treat the query as a regular expression." },
      },
      required: ["query"],
      additionalProperties: false,
    },
  },
  {
    name: "everything_status",
    description:
      "Report whether Everything is running and queryable, its version, and whether its index is loaded. " +
      "Call this first if a search fails, to tell 'Everything is not running' apart from 'no matches'.",
    inputSchema: { type: "object", properties: {}, additionalProperties: false },
  },
  {
    name: "everything_recent",
    description:
      "List the most recently modified files matching an optional query. A convenience wrapper over " +
      "everything_search with a date-modified sort, for 'what did I just change' questions.",
    inputSchema: {
      type: "object",
      properties: {
        query: { type: "string", description: "Optional Everything query to narrow the set (default: all files)." },
        max: { type: "integer", description: "Rows to return (default 20, max 200).", minimum: 1, maximum: 200 },
        sinceDays: {
          type: "integer",
          description: "Only include items modified within this many days (default 7).",
          minimum: 1,
          maximum: 3650,
        },
      },
      additionalProperties: false,
    },
  },
  {
    name: "everything_duplicates",
    description:
      "Find files sharing the same name across the disk -- useful for locating stale copies, duplicated " +
      "downloads, or several builds of the same binary. Returns one entry per distinct name with its " +
      "occurrence count and all full paths.",
    inputSchema: {
      type: "object",
      properties: {
        query: { type: "string", description: "Narrow the search first, e.g. 'ext:dll' or 'D:\\projects *.config'." },
        name: { type: "string", description: "Exact file name to check across the whole index." },
        minCopies: { type: "integer", description: "Only report names with at least this many copies (default 2).", minimum: 2 },
        maxNames: { type: "integer", description: "Maximum distinct names to report (default 20, max 200).", minimum: 1, maximum: 200 },
        maxScan: { type: "integer", description: "How many matching rows to scan for duplicates (default 5000, max 50000).", minimum: 1, maximum: 50000 },
      },
      additionalProperties: false,
    },
  },
];

// ---------------------------------------------------------------- handlers

function buildQueryOptions(args, maxCap = 300) {
  const options = {
    search: args.query ?? "",
    max: Math.min(Math.max(args.max ?? 50, 1), maxCap),
    offset: Math.max(args.offset ?? 0, 0),
    sort: args.sort ?? "name_asc",
    matchPath: args.matchPath === true,
    matchCase: args.matchCase === true,
    wholeWord: args.wholeWord === true,
    regex: args.regex === true,
    fields: ["all"],
  };
  return options;
}

function requireOk(payload) {
  if (!payload || payload.ok !== true) {
    const detail = payload?.error ?? "no response";
    throw new Error(
      /not running/i.test(detail)
        ? `Everything is not running, so its index is unavailable (${detail}). ` +
          "Start it with: Start-Process 'C:\\Program Files (x86)\\Everything\\Everything.exe'"
        : detail,
    );
  }
  return payload;
}

/**
 * One cheap state read before a query, because Everything answers from a partial
 * database while it rebuilds the index and reports nothing. Measured: a query that
 * returns 6,769 rows on a settled index returned 66 during a rebuild. Marking the
 * result beats letting a model conclude the files are gone.
 */
async function indexWarning() {
  try {
    const payload = await bridge.request({ cmd: "version" }, 8000);
    if (payload?.ok === true && payload.dbBusy === true) {
      return "⚠ Everything is rebuilding its index right now — these results may be incomplete.";
    }
  } catch {
    // The query that follows reports the real failure; do not mask it with this.
  }
  return null;
}

async function handleSearch(args, { maxCap = 300 } = {}) {
  const options = buildQueryOptions(args, maxCap);
  // file:/folder: are filters inside the index. Applying them to an already-paged
  // slice instead would break paging: short or empty pages, and an offset that no
  // longer addresses the rows the caller thinks it does.
  const prefix = args.foldersOnly === true ? "folder:" : args.filesOnly === true ? "file:" : "";
  const search = prefix ? `${prefix} ${options.search}` : options.search;
  const warning = await indexWarning();
  const payload = requireOk(await bridge.request({ cmd: "search", ...options, search }));
  return summarize(payload, `Everything search "${search}"`, warning);
}

async function handleCount(args) {
  const warning = await indexWarning();
  const payload = requireOk(await bridge.request({
    cmd: "count",
    search: args.query ?? "",
    matchPath: args.matchPath === true,
    matchCase: args.matchCase === true,
    regex: args.regex === true,
  }));
  const noun = payload.total === 1 ? "item" : "items";
  const text = `Everything reports ${payload.total} matching ${noun} for "${payload.search}".`;
  return warning ? `${warning}\n${text}` : text;
}

async function handleStatus() {
  const payload = await bridge.request({ cmd: "version" }, 20000);
  if (!payload.ok) {
    return `Everything is NOT available: ${payload.error}\n` +
      "Start it with: Start-Process 'C:\\Program Files (x86)\\Everything\\Everything.exe'";
  }
  return [
    `Everything ${payload.version} is running and queryable.`,
    `  index loaded: ${payload.dbLoaded}`,
    `  index busy (rebuilding): ${payload.dbBusy}`,
    `  running as admin: ${payload.runningAsAdmin}`,
  ].join("\n");
}

async function handleRecent(args) {
  const days = Math.min(Math.max(args.sinceDays ?? 7, 1), 3650);
  // Everything's date syntax. The `last` prefix is load-bearing and MUST be kept:
  // measured on Everything 1.4.1.1032, `dm:7days` does NOT mean "the last 7 days".
  // It matched 317 items ~7,052 days old, while `dm:last7days` matched 214,014
  // recent ones. The official docs' `dm:7days` example is not portable, so do not
  // "simplify" these terms by dropping the prefix.
  //
  // `dm:lastNdays` is also used for the 2..7 range rather than `dm:thisweek`,
  // because `thisweek` means the current calendar week (which on a Monday is a
  // one-day window), not a rolling 7 days. Verified monotonic on this build:
  // last1days 77,908 -> last7days 214,015 -> last14days 289,978 -> last30days 417,686.
  const dateTerm = days <= 1 ? "dm:today" : `dm:last${days}days`;

  // `file:` keeps directories out of a list this tool describes as files — and it
  // filters inside the index, so paging stays honest (see handleSearch).
  const query = ["file:", args.query, dateTerm]
    .filter((part) => part && part.length > 0)
    .join(" ");

  const warning = await indexWarning();
  const payload = requireOk(await bridge.request({
    cmd: "search",
    search: query,
    max: Math.min(Math.max(args.max ?? 20, 1), 200),
    sort: "date_modified_desc",
    fields: ["all"],
  }));
  return summarize(payload, `Recently modified (${dateTerm}; query "${query}")`, warning);
}

async function handleDuplicates(args) {
  const scan = Math.min(Math.max(args.maxScan ?? 5000, 1), 50000);
  const query = args.name ? `"${args.name}"` : (args.query ?? "");
  const payload = requireOk(await bridge.request({
    cmd: "search",
    search: query,
    max: scan,
    sort: "name_asc",
    fields: ["name", "path", "fullpath", "size", "dateModified"],
  }));

  const byName = new Map();
  for (const item of payload.results) {
    if (item.isFolder) continue;
    const key = item.name ?? item.fullPath;
    if (!byName.has(key)) byName.set(key, []);
    byName.get(key).push(item);
  }

  const minCopies = Math.max(args.minCopies ?? 2, 2);
  const groups = [...byName.entries()]
    .filter(([, items]) => items.length >= minCopies)
    .sort((a, b) => b[1].length - a[1].length)
    .slice(0, Math.min(Math.max(args.maxNames ?? 20, 1), 200));

  const warning = await indexWarning();
  const lead = warning ? `${warning}\n` : "";

  if (groups.length === 0) {
    return lead + `No duplicate file names found (scanned ${payload.results.length} of ${payload.total} ` +
      `matching rows, minimum ${minCopies} copies).`;
  }

  const lines = [`Duplicate file names (scanned ${payload.results.length} of ${payload.total} rows):`];
  for (const [name, items] of groups) {
    lines.push(`\n${name}  -- ${items.length} copies`);
    for (const item of items) {
      const size = item.size !== null && item.size !== undefined ? ` (${formatSize(item.size)})` : "";
      lines.push(`    ${item.fullPath}${size}`);
    }
  }
  if (payload.total > payload.results.length) {
    lines.push(`\nNote: only the first ${payload.results.length} of ${payload.total} matching rows were ` +
      "scanned; narrow the query or raise maxScan for a complete picture.");
  }
  return lead + lines.join("\n");
}

const HANDLERS = {
  everything_search: handleSearch,
  everything_count: handleCount,
  everything_status: handleStatus,
  everything_recent: handleRecent,
  everything_duplicates: handleDuplicates,
};

// ------------------------------------------------------------ MCP protocol

// JSON-RPC over stdio. Responses are newline-delimited JSON; notifications omit
// the id field. Anything the model should not see goes to stderr instead.

function send(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

function sendResult(id, result) {
  send({ jsonrpc: "2.0", id, result });
}

function sendError(id, code, message) {
  send({ jsonrpc: "2.0", id, error: { code, message } });
}

const INSTRUCTIONS =
  "Everything indexes file and folder names on the volumes it has been told to index, and answers " +
  "name queries immediately. Results are paths and metadata only, never file contents, and an empty " +
  "result means 'not in the current index', not 'does not exist'.\n\n" +
  "FILE LOCATION IS THIS SERVER'S JOB. When the user asks where a file is, when you need to find " +
  "files by name, extension, size, or date, or when the target may be outside the working " +
  "directory, call everything_search FIRST — before glob, before any shell command, before " +
  "guessing a path. Do not walk directories by hand to locate a file.\n\n" +
  "Use glob instead when you need an exact, complete, workspace-relative enumeration: glob never " +
  "truncates, while everything_search returns a top-N page and states clearly when the list is " +
  "incomplete.\n\n" +
  "The index covers names and metadata, not file contents. For searching text INSIDE files, use " +
  "grep, not this server.";

async function handleMessage(message) {
  const { id, method, params } = message;
  const isNotification = id === undefined || id === null;

  try {
    switch (method) {
      case "initialize":
        sendResult(id, {
          protocolVersion: params?.protocolVersion ?? "2024-11-05",
          capabilities: { tools: {} },
          serverInfo: { name: SERVER_NAME, version: SERVER_VERSION },
          instructions: INSTRUCTIONS,
        });
        return;

      case "notifications/initialized":
      case "notifications/cancelled":
        return;

      case "ping":
        sendResult(id, {});
        return;

      case "tools/list":
        sendResult(id, { tools: TOOLS });
        return;

      case "tools/call": {
        const name = params?.name;
        const handler = HANDLERS[name];
        if (!handler) {
          sendError(id, -32602, `unknown tool: ${name}`);
          return;
        }
        try {
          const text = await handler(params?.arguments ?? {});
          sendResult(id, { content: [{ type: "text", text: clampText(text) }] });
        } catch (error) {
          // Tool failures are results, not protocol errors, so the model sees them.
          sendResult(id, {
            content: [{ type: "text", text: `Error: ${error.message}` }],
            isError: true,
          });
        }
        return;
      }

      default:
        if (isNotification) return;
        sendError(id, -32601, `method not found: ${method}`);
    }
  } catch (error) {
    if (!isNotification) sendError(id, -32603, error.message);
    else log(`notification ${method} failed: ${error.message}`);
  }
}

function runStdioServer() {
  let buffer = "";
  process.stdin.setEncoding("utf8");
  process.stdin.on("data", (chunk) => {
    buffer += chunk;
    let index;
    while ((index = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, index).trim();
      buffer = buffer.slice(index + 1);
      if (!line) continue;
      let message;
      try {
        message = JSON.parse(line);
      } catch {
        log(`ignoring non-JSON input: ${line.slice(0, 200)}`);
        continue;
      }
      // Deliberately not awaited: a slow query must not block the next request.
      handleMessage(message).catch((error) => log(`handler failed: ${error.message}`));
    }
  });

  process.stdin.on("end", shutdown);
  process.stdin.on("error", shutdown);
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);

  // Parent-death guard. If the harness is killed without closing the pipes, this
  // process is re-parented and would otherwise linger forever holding the stdio
  // bridge open -- observed in practice while developing this server.
  const originalParent = process.ppid;
  const parentWatch = setInterval(() => {
    if (process.ppid !== originalParent) {
      log("parent process exited; shutting down");
      shutdown();
    }
  }, 5000);
  parentWatch.unref();

  log(`ready (bridge: ${BRIDGE}, parent ${originalParent})`);
}

let shuttingDown = false;
function shutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  bridge.stop();
  process.exit(0);
}

// ---------------------------------------------------------------- CLI mode

const CLI_SORTS = [
  "name_asc", "name_desc", "path_asc", "path_desc", "size_asc", "size_desc",
  "ext_asc", "ext_desc", "date_modified_asc", "date_modified_desc",
  "date_created_asc", "date_created_desc", "date_accessed_asc", "date_accessed_desc",
  "runcount_asc", "runcount_desc",
];

const CLI_USAGE = [
  "usage: everything-mcp.mjs [status | search <query> [options]]",
  "",
  "  --max N         rows to fetch, 1-1000 (default 20)",
  "  --offset N      skip N rows (default 0)",
  `  --sort NAME     ${CLI_SORTS.join(", ")}`,
  "  --regex         treat <query> as a regular expression",
  "  --match-case    case-sensitive matching",
  "  --whole-word    whole-word matching",
  "  --match-path    match the full path instead of the file name",
  "  --files-only    files only",
  "  --folders-only  folders only",
].join("\n");

function cliError(message) {
  process.stderr.write(`${message}\n\n${CLI_USAGE}\n`);
  process.exitCode = 2;
}

async function runCli(argv) {
  const command = argv[0] ?? "status";

  if (command === "status") {
    if (argv.length > 1) {
      cliError(`status takes no arguments, got: ${argv.slice(1).join(" ")}`);
      return;
    }
    process.stdout.write(`${await handleStatus()}\n`);
    bridge.stop();
    return;
  }

  if (command !== "search") {
    cliError(`unknown command "${command}"`);
    return;
  }

  // Every flag the MCP tool accepts is accepted here too, and nothing else. A
  // silently dropped flag changes what the query means while still printing a
  // normal-looking result, which is worse than refusing to run.
  const args = { query: "", max: 20 };
  const positional = [];

  for (let i = 1; i < argv.length; i += 1) {
    const token = argv[i];
    if (!token.startsWith("--")) {
      positional.push(token);
      continue;
    }

    const takeValue = () => {
      const value = argv[i + 1];
      if (value === undefined || value.startsWith("--")) {
        cliError(`${token} needs a value`);
        return null;
      }
      i += 1;
      return value;
    };

    switch (token) {
      case "--max":
      case "--offset": {
        const raw = takeValue();
        if (raw === null) return;
        const value = Number(raw);
        const min = token === "--max" ? 1 : 0;
        const max = token === "--max" ? 1000 : Number.MAX_SAFE_INTEGER;
        if (!Number.isInteger(value) || value < min || value > max) {
          cliError(`${token} must be an integer ${min}-${max}, got "${raw}"`);
          return;
        }
        args[token.slice(2)] = value;
        break;
      }
      case "--sort": {
        const raw = takeValue();
        if (raw === null) return;
        if (!CLI_SORTS.includes(raw)) {
          cliError(`unknown sort "${raw}"`);
          return;
        }
        args.sort = raw;
        break;
      }
      case "--regex": args.regex = true; break;
      case "--match-case": args.matchCase = true; break;
      case "--whole-word": args.wholeWord = true; break;
      case "--match-path": args.matchPath = true; break;
      case "--files-only": args.filesOnly = true; break;
      case "--folders-only": args.foldersOnly = true; break;
      default:
        cliError(`unknown option "${token}"`);
        return;
    }
  }

  if (positional.length > 1) {
    cliError(`unexpected argument${positional.length > 2 ? "s" : ""}: ${positional.slice(1).join(" ")}`);
    return;
  }

  args.query = positional[0] ?? "";
  // The MCP surface caps a page at 300 rows because those rows land in the model's
  // context. CLI output goes to a terminal or a file, so it keeps the wider page
  // and is never character-clamped.
  const text = await handleSearch(args, { maxCap: 1000 });
  process.stdout.write(`${text}\n`);
  bridge.stop();
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1];
if (isMain && process.argv.length > 2) {
  runCli(process.argv.slice(2)).catch((error) => {
    process.stderr.write(`[everything] ${error.message}\n`);
    process.exit(1);
  });
} else {
  runStdioServer();
}
