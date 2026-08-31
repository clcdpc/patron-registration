import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { test } from "node:test";
import {
    hasPriorReleaseValidationRun,
    isFirstWorkflowAttempt,
    isNewReleaseTagPush,
    parseWorkflowRunHistory
} from "./release-validation-guard.mjs";

const tag = "v1.2.3";

function creationEvent(overrides = {}) {
    return {
        ref: `refs/tags/${tag}`,
        tag,
        created: "true",
        deleted: "false",
        forced: "false",
        ...overrides
    };
}

function run(id, headBranch, overrides = {}) {
    return {
        id,
        head_branch: headBranch,
        event: "push",
        status: "completed",
        conclusion: "success",
        ...overrides
    };
}

function history(...runs) {
    return [{ workflow_runs: runs }];
}

test("initial creation of v1.2.3 is eligible", () => {
    assert.equal(isNewReleaseTagPush(creationEvent()), true);
    assert.equal(isFirstWorkflowAttempt("1"), true);
    assert.equal(hasPriorReleaseValidationRun(history(run("200", tag)), tag, "200"), false);
});

test("ordinary GitHub rerun is rejected", () => {
    assert.equal(isFirstWorkflowAttempt("2"), false);
    assert.equal(isFirstWorkflowAttempt(2), false);
});

test("force-update or move of v1.2.3 is rejected", () => {
    assert.equal(isNewReleaseTagPush(creationEvent({ created: "false", forced: "true" })), false);
    assert.equal(isNewReleaseTagPush(creationEvent({ created: "false", forced: "false" })), false);
});

test("tag deletion is rejected", () => {
    assert.equal(isNewReleaseTagPush(creationEvent({ created: "false", deleted: "true" })), false);
});

test("deletion then recreation is rejected when prior tag history exists", () => {
    const recreated = creationEvent();
    assert.equal(isNewReleaseTagPush(recreated), true);
    assert.equal(hasPriorReleaseValidationRun(
        history(run("200", tag), run("201", tag)),
        tag,
        "201"), true);
});

test("a different release tag remains eligible", () => {
    const otherTag = "v1.2.4";
    assert.equal(hasPriorReleaseValidationRun(history(run("200", otherTag)), tag, "201"), false);
});

test("strict one-run policy blocks a prior run that failed before live execution", () => {
    assert.equal(hasPriorReleaseValidationRun(
        history(run("200", tag, { conclusion: "failure" })),
        tag,
        "201"), true);
});

test("failure to inspect prior-run history fails closed", () => {
    assert.throws(() => parseWorkflowRunHistory("not-json"));
    assert.throws(() => parseWorkflowRunHistory(JSON.stringify([{ workflow_runs: [{}] }])));

    const result = spawnSync(
        process.execPath,
        [".github/scripts/release-validation-guard.mjs", "history", tag, "201"],
        { input: "not-json", encoding: "utf8" });
    assert.notEqual(result.status, 0);
});
