import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

export const releaseTagPattern = /^v\d+\.\d+\.\d+(?:[.-][0-9A-Za-z.-]+)?$/;

function isRecord(value) {
    return value !== null && typeof value === "object" && !Array.isArray(value);
}

function isBoolean(value, expected) {
    return value === expected || value === String(expected);
}

function requireReleaseTag(tag) {
    if (typeof tag !== "string" || !releaseTagPattern.test(tag)) {
        throw new Error("The release tag is not a valid version tag.");
    }
}

export function isNewReleaseTagPush(event) {
    if (!isRecord(event) || typeof event.ref !== "string" || typeof event.tag !== "string") {
        return false;
    }

    if (!releaseTagPattern.test(event.tag) || event.ref !== `refs/tags/${event.tag}`) {
        return false;
    }

    return isBoolean(event.created, true) &&
        isBoolean(event.deleted, false) &&
        isBoolean(event.forced, false);
}

export function isFirstWorkflowAttempt(runAttempt) {
    return runAttempt === 1 || runAttempt === "1";
}

export function parseWorkflowRunHistory(json) {
    const pages = JSON.parse(json);
    if (!Array.isArray(pages) || pages.length === 0) {
        throw new Error("Workflow-run history was not a non-empty paginated response.");
    }

    for (const page of pages) {
        if (!isRecord(page) || !Array.isArray(page.workflow_runs)) {
            throw new Error("Workflow-run history contained an invalid page.");
        }

        for (const run of page.workflow_runs) {
            if (!isRecord(run) || !Object.hasOwn(run, "id") || !Object.hasOwn(run, "head_branch")) {
                throw new Error("Workflow-run history contained an invalid run.");
            }
            if ((typeof run.id !== "string" && typeof run.id !== "number") || String(run.id).length === 0) {
                throw new Error("Workflow-run history contained a run without a usable id.");
            }
            if (run.head_branch !== null && typeof run.head_branch !== "string") {
                throw new Error("Workflow-run history contained a run without a usable head ref.");
            }
        }
    }

    return pages;
}

export function hasPriorReleaseValidationRun(pages, releaseTag, currentRunId) {
    requireReleaseTag(releaseTag);
    if (currentRunId === undefined || currentRunId === null || String(currentRunId).length === 0) {
        throw new Error("The current workflow-run id is required.");
    }

    const currentId = String(currentRunId);
    return pages.some(page => page.workflow_runs.some(run =>
        String(run.id) !== currentId && run.head_branch === releaseTag));
}

function eventFromEnvironment() {
    return {
        ref: process.env.EVENT_REF,
        tag: process.env.EVENT_TAG,
        created: process.env.EVENT_CREATED,
        deleted: process.env.EVENT_DELETED,
        forced: process.env.EVENT_FORCED
    };
}

function main() {
    const [mode, ...args] = process.argv.slice(2);

    if (mode === "event" && args.length === 0) {
        return isNewReleaseTagPush(eventFromEnvironment()) ? 0 : 1;
    }

    if (mode === "history" && args.length === 2) {
        const pages = parseWorkflowRunHistory(readFileSync(0, "utf8"));
        const blocked = hasPriorReleaseValidationRun(pages, args[0], args[1]);
        process.stdout.write(blocked ? "blocked\n" : "eligible\n");
        return 0;
    }

    throw new Error("Usage: release-validation-guard.mjs event|history <tag> <run-id>");
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
    try {
        process.exitCode = main();
    } catch (error) {
        console.error(error instanceof Error ? error.message : String(error));
        process.exitCode = 2;
    }
}
