#!/usr/bin/env python3
# Copyright (c) 2026 Justin Wojciechowski.
# Licensed under the MIT License.

"""
CI orchestrator for iOS Simulator tests in swift-dotnet-packages.

Manages the full lifecycle: simulator creation, parallel boot + build,
test execution via validate-sim.sh, diagnostics collection, and cleanup.
Designed to replace inline bash/YAML in GitHub Actions workflows.

Usage:
    # Full pipeline for a library test
    python3 ci_ios_test.py --test-dir tests/Nuke.SimTests

    # With specific timeout and step budget
    python3 ci_ios_test.py --test-dir tests/Nuke.SimTests --timeout 30 --step-timeout 1200

    # Reuse existing simulator
    python3 ci_ios_test.py --test-dir tests/Nuke.SimTests --reuse-device

    # Just prepare simulator and output UDID
    python3 ci_ios_test.py --prepare-only

    # Use pre-booted simulator
    python3 ci_ios_test.py --test-dir tests/Nuke.SimTests --device-udid <UDID> --skip-prepare
"""

import argparse
import json
import logging
import os
import re
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor, Future
from pathlib import Path
from typing import Optional

# Add parent directory so we can import sim_manager
sys.path.insert(0, str(Path(__file__).parent))
from sim_manager import (
    SimManager, SimConfig, SimError,
    SimulatorBootTimeout, SimulatorReadinessTimeout, SimulatorNotFound,
)

log = logging.getLogger("ci_ios_test")

# ---------------------------------------------------------------------------
# Error classification
# ---------------------------------------------------------------------------

# Patterns in stderr/output that indicate infrastructure (retryable) failures
INFRA_FAILURE_PATTERNS = [
    "failed to boot",
    "unable to boot",
    "unable to lookup in current state",
    "CoreSimulatorService connection interrupted",
    "timed out waiting",
    "launchd",
    "bootstrap",
    "SimulatorBootTimeout",
    "SimulatorReadinessTimeout",
    "domain error",
    "Unable to negotiate with CoreSimulatorService",
]


def is_infra_failure(error: Exception) -> bool:
    """Classify whether an error is retryable infrastructure vs real test failure."""
    if isinstance(error, (SimulatorBootTimeout, SimulatorReadinessTimeout, SimulatorNotFound)):
        return True
    msg = str(error).lower()
    return any(pat.lower() in msg for pat in INFRA_FAILURE_PATTERNS)


# ---------------------------------------------------------------------------
# Test directory introspection
# ---------------------------------------------------------------------------

def resolve_app_name(test_dir: str) -> Optional[str]:
    """Extract the app name from validate-sim.sh in the test directory."""
    validate_script = os.path.join(test_dir, "validate-sim.sh")
    if not os.path.isfile(validate_script):
        return None
    try:
        with open(validate_script) as f:
            content = f.read()
        match = re.search(r'APP_NAME="([^"]+)"', content)
        if match:
            return match.group(1)
    except Exception:
        pass
    return None


def resolve_bundle_id(test_dir: str) -> Optional[str]:
    """Extract the bundle ID from validate-sim.sh in the test directory."""
    validate_script = os.path.join(test_dir, "validate-sim.sh")
    if not os.path.isfile(validate_script):
        return None
    try:
        with open(validate_script) as f:
            content = f.read()
        match = re.search(r'BUNDLE_ID="([^"]+)"', content)
        if match:
            return match.group(1)
    except Exception:
        pass
    return None


# ---------------------------------------------------------------------------
# Build step
# ---------------------------------------------------------------------------

def run_build(test_dir: str) -> None:
    """Run build-testapp.sh in the test directory."""
    log.info("=== BUILD: Starting build-testapp.sh ===")
    build_script = os.path.join(test_dir, "build-testapp.sh")

    if not os.path.isfile(build_script):
        raise RuntimeError(f"build-testapp.sh not found in {test_dir}")

    result = subprocess.run(
        ["bash", build_script],
        cwd=test_dir,
        capture_output=True,
        text=True,
        timeout=420,
    )

    if result.stdout:
        print(result.stdout, end="", flush=True)
    if result.returncode != 0:
        log.error("Build failed:\n%s", result.stderr[-2000:] if result.stderr else "(no stderr)")
        raise RuntimeError(f"build-testapp.sh failed with exit code {result.returncode}")

    log.info("=== BUILD: Success ===")


# ---------------------------------------------------------------------------
# Test execution
# ---------------------------------------------------------------------------

def run_tests(
    test_dir: str,
    device_udid: str,
    timeout: int = 30,
    max_test_retries: int = 1,
    deadline: Optional[float] = None,
) -> int:
    """Run validate-sim.sh with retry logic.

    Retries once on timeout/infrastructure failure, but only if enough
    time remains before the deadline.

    Returns:
        Exit code from validate-sim.sh (0 = success)
    """
    validate_script = os.path.join(test_dir, "validate-sim.sh")
    if not os.path.isfile(validate_script):
        raise RuntimeError(f"validate-sim.sh not found in {test_dir}")

    bundle_id = resolve_bundle_id(test_dir)
    BUILD_OVERHEAD = 300  # seconds — rebuild overhead if we need to retry
    last_output = ""

    for attempt in range(1, max_test_retries + 2):
        if attempt > 1:
            # Check if we have enough time for a retry
            min_retry_time = timeout + 30  # test timeout + some buffer
            if deadline is not None:
                remaining = deadline - time.time()
                if remaining < min_retry_time:
                    log.warning(
                        "Only %.0fs remaining (need %ds for retry) — skipping retry",
                        remaining, min_retry_time,
                    )
                    return 1
                log.info("%.0fs remaining — enough for retry (need %ds)", remaining, min_retry_time)

            # Terminate stale app if possible
            if bundle_id:
                try:
                    mgr = SimManager()
                    mgr.terminate_app(device_udid, bundle_id)
                except Exception:
                    pass

            time.sleep(2)  # Let simulator settle
            log.info("=== TESTS: Retry attempt %d (previous run timed out) ===", attempt)
            gha_warning(f"Test retry attempt {attempt} after timeout/hang")

        # Calculate subprocess timeout
        if deadline is not None:
            remaining = deadline - time.time()
            subprocess_timeout = min(timeout + BUILD_OVERHEAD, max(remaining - 30, timeout + 60))
        else:
            subprocess_timeout = timeout + BUILD_OVERHEAD

        log.info(
            "=== TESTS: Running validate-sim.sh (timeout=%ds, attempt=%d, subprocess_timeout=%.0fs) ===",
            timeout, attempt, subprocess_timeout,
        )

        try:
            result = subprocess.run(
                ["bash", validate_script, str(timeout), device_udid],
                cwd=test_dir,
                capture_output=True,
                text=True,
                timeout=subprocess_timeout,
            )

            last_output = result.stdout or ""
            if result.stdout:
                print(result.stdout, end="", flush=True)
            if result.stderr:
                print(result.stderr, end="", file=sys.stderr, flush=True)

            if result.returncode == 0:
                log.info("=== TESTS: PASSED ===")
                return 0

            # Check if this looks like a timeout (retryable) vs crash/real failure
            if "TIMEOUT" in last_output and attempt <= max_test_retries:
                log.warning("Tests timed out — will retry")
                continue

            # Crash or real test failure — don't retry
            log.error("=== TESTS: FAILED (exit code %d) ===", result.returncode)
            return result.returncode

        except subprocess.TimeoutExpired as e:
            if e.stdout:
                last_output = e.stdout if isinstance(e.stdout, str) else e.stdout.decode()
                print(last_output, end="", flush=True)
            else:
                last_output = ""
            if attempt <= max_test_retries:
                log.warning("Test subprocess timed out — will retry")
                continue
            log.error("=== TESTS: TIMED OUT (subprocess) ===")
            return 1

    return 1  # Should not reach here


# ---------------------------------------------------------------------------
# GitHub Actions helpers
# ---------------------------------------------------------------------------

def set_gha_output(name: str, value: str) -> None:
    """Set a GitHub Actions output variable."""
    output_file = os.environ.get("GITHUB_OUTPUT")
    if output_file:
        with open(output_file, "a") as f:
            f.write(f"{name}={value}\n")
        log.info("Set GHA output: %s=%s", name, value)
    else:
        log.debug("Not in GHA environment, skipping output: %s=%s", name, value)


def set_gha_env(name: str, value: str) -> None:
    """Set a GitHub Actions environment variable for subsequent steps."""
    env_file = os.environ.get("GITHUB_ENV")
    if env_file:
        with open(env_file, "a") as f:
            f.write(f"{name}={value}\n")


def gha_group(title: str) -> None:
    """Start a GitHub Actions log group."""
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::group::{title}", flush=True)


def gha_endgroup() -> None:
    """End a GitHub Actions log group."""
    if os.environ.get("GITHUB_ACTIONS"):
        print("::endgroup::", flush=True)


def gha_error(message: str) -> None:
    """Emit a GitHub Actions error annotation."""
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::error::{message}", flush=True)


def gha_warning(message: str) -> None:
    """Emit a GitHub Actions warning annotation."""
    if os.environ.get("GITHUB_ACTIONS"):
        print(f"::warning::{message}", flush=True)


# ---------------------------------------------------------------------------
# Orchestrator
# ---------------------------------------------------------------------------

def run_pipeline(
    test_dir: str,
    runtime_prefix: Optional[str] = None,
    device_name: Optional[str] = None,
    device_udid: Optional[str] = None,
    create_fresh: bool = True,
    prepare_only: bool = False,
    skip_prepare: bool = False,
    skip_build: bool = False,
    test_timeout: int = 30,
    max_infra_retries: int = 1,
    diag_dir: str = "/tmp/sim-diagnostics",
    step_timeout: int = 900,
) -> int:
    """Full CI pipeline: prepare simulator, build, test, cleanup.

    Args:
        test_dir: Path to the test directory (e.g. tests/Nuke.SimTests).
        step_timeout: Total wall-clock budget in seconds (matches the GHA
                      timeout-minutes value). Used to compute a deadline so
                      retries are skipped when insufficient time remains.

    Returns:
        0 on success, non-zero on failure.
    """
    pipeline_start = time.time()
    deadline = pipeline_start + step_timeout
    mgr = SimManager()
    created_udid = None
    app_name = resolve_app_name(test_dir)

    for attempt in range(1, max_infra_retries + 2):
        try:
            if attempt > 1:
                log.info("")
                log.info("=" * 60)
                log.info("RETRY attempt %d/%d (infrastructure failure)", attempt, max_infra_retries + 1)
                log.info("=" * 60)

            # Phase 1: Prepare simulator (parallel with build)
            if not skip_prepare and not device_udid:
                if skip_build:
                    # Sequential: just prepare the simulator
                    gha_group("Prepare iOS Simulator")
                    log.info("=== PREPARE: Creating and booting simulator ===")
                    created_udid = mgr.prepare_simulator(
                        runtime_prefix, device_name,
                        create_fresh=create_fresh,
                    )
                    device_udid = created_udid
                    gha_endgroup()
                else:
                    # Parallel: boot simulator + build simultaneously
                    gha_group("Parallel: Boot Simulator + Build Test App")
                    log.info("=== PARALLEL: Starting simulator boot + test app build ===")

                    with ThreadPoolExecutor(max_workers=2) as executor:
                        sim_future: Future = executor.submit(
                            mgr.prepare_simulator,
                            runtime_prefix, device_name,
                            create_fresh,
                        )
                        build_future: Future = executor.submit(
                            run_build, test_dir,
                        )

                        # Wait for both, collect errors
                        errors = []
                        for name, future in [("simulator", sim_future), ("build", build_future)]:
                            try:
                                result = future.result(timeout=480)
                                if name == "simulator":
                                    created_udid = result
                                    device_udid = result
                                    log.info("Simulator ready: %s", device_udid)
                            except Exception as e:
                                log.error("%s failed: %s", name, e)
                                errors.append((name, e))

                        if errors:
                            # If simulator failed, raise that (possibly retryable)
                            for name, err in errors:
                                if name == "simulator":
                                    raise err
                            # Otherwise build failed (not retryable)
                            _, err = errors[0]
                            raise err

                    gha_endgroup()
                    skip_build = True  # Don't rebuild on retry

                set_gha_output("device_udid", device_udid)
                set_gha_env("SIM_UDID", device_udid)
                log.info("Simulator UDID: %s", device_udid)

            elif device_udid:
                log.info("Using provided simulator: %s", device_udid)

            if prepare_only:
                log.info("=== PREPARE-ONLY: Simulator ready, exiting ===")
                print(device_udid)
                return 0

            # Phase 2: Build (if not already done in parallel)
            if not skip_build:
                gha_group("Build Test App")
                run_build(test_dir)
                gha_endgroup()
                skip_build = True

            # Phase 3: Run tests
            gha_group("Run iOS Simulator Tests")
            exit_code = run_tests(
                test_dir,
                device_udid,
                timeout=test_timeout,
                deadline=deadline,
            )
            gha_endgroup()

            if exit_code == 0:
                return 0

            # Test failed — check if it's infra or real
            return exit_code

        except Exception as e:
            log.error("Pipeline error: %s", e)

            if is_infra_failure(e) and attempt <= max_infra_retries:
                gha_warning(f"Infrastructure failure (attempt {attempt}): {e}")
                log.info("Collecting diagnostics before retry...")
                if created_udid:
                    try:
                        mgr.collect_diagnostics(created_udid, diag_dir, app_name)
                    except Exception as diag_err:
                        log.warning("Diagnostics collection failed: %s", diag_err)
                    mgr.cleanup(created_udid)
                    created_udid = None
                    device_udid = None
                continue

            # Non-retryable or out of retries
            gha_error(f"Pipeline failed: {e}")
            if created_udid:
                log.info("Collecting diagnostics...")
                try:
                    mgr.collect_diagnostics(created_udid, diag_dir, app_name)
                except Exception as diag_err:
                    log.warning("Diagnostics collection failed: %s", diag_err)
            return 1

        finally:
            # Cleanup on the last attempt or on success
            # (Don't cleanup on retry — we do it explicitly above)
            if attempt > max_infra_retries or not is_infra_failure(sys.exc_info()[1] or Exception()):
                if created_udid:
                    gha_group("Cleanup Simulator")
                    mgr.cleanup(created_udid)
                    gha_endgroup()

    return 1  # Should not reach here


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description="CI orchestrator for iOS Simulator tests",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Full pipeline (parallel boot+build, test, cleanup)
  python3 ci_ios_test.py --test-dir tests/Nuke.SimTests

  # Just prepare simulator and output UDID
  python3 ci_ios_test.py --prepare-only

  # Run tests with pre-booted simulator
  python3 ci_ios_test.py --test-dir tests/Nuke.SimTests --device-udid ABC123 --skip-prepare

  # Use existing device instead of creating fresh
  python3 ci_ios_test.py --test-dir tests/Nuke.SimTests --reuse-device
""",
    )

    # Simulator options
    sim_group = parser.add_argument_group("simulator")
    sim_group.add_argument("--runtime", help="iOS runtime prefix (e.g. iOS-18)")
    sim_group.add_argument("--device-type", help="Device name (e.g. 'iPhone 16')")
    sim_group.add_argument("--device-udid", help="Use pre-booted simulator UDID")
    sim_group.add_argument("--reuse-device", action="store_true",
                          help="Reuse existing device instead of creating fresh")
    sim_group.add_argument("--prepare-only", action="store_true",
                          help="Only prepare simulator, print UDID, and exit")
    sim_group.add_argument("--skip-prepare", action="store_true",
                          help="Skip simulator preparation (requires --device-udid)")

    # Build/test options
    test_group = parser.add_argument_group("test")
    test_group.add_argument("--test-dir", required=False,
                           help="Path to test directory (e.g. tests/Nuke.SimTests)")
    test_group.add_argument("--timeout", type=int, default=30,
                           help="Test timeout in seconds passed to validate-sim.sh (default: 30)")
    test_group.add_argument("--skip-build", action="store_true",
                           help="Skip build-testapp.sh (app already built)")

    # Resilience options
    resilience_group = parser.add_argument_group("resilience")
    resilience_group.add_argument("--max-retries", type=int, default=1,
                                 help="Max infrastructure retries (default: 1)")
    resilience_group.add_argument("--diag-dir", default="/tmp/sim-diagnostics",
                                 help="Directory for diagnostic artifacts")
    resilience_group.add_argument("--step-timeout", type=int, default=1200,
                                 help="Total wall-clock budget in seconds (default: 1200 = 20 min)")

    # Logging
    parser.add_argument("-v", "--verbose", action="store_true", help="Debug logging")

    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
    )

    if args.skip_prepare and not args.device_udid:
        parser.error("--skip-prepare requires --device-udid")

    if not args.prepare_only and not args.test_dir:
        parser.error("--test-dir is required unless using --prepare-only")

    # Resolve test directory
    test_dir = args.test_dir
    if test_dir and not os.path.isabs(test_dir):
        if not os.path.isdir(test_dir):
            # Try relative to repo root (script is at scripts/ci/)
            script_dir = Path(__file__).parent
            repo_root = script_dir.parent.parent
            test_dir = str(repo_root / test_dir)

    if test_dir and not args.prepare_only and not os.path.isdir(test_dir):
        log.error("Test directory not found: %s", test_dir)
        sys.exit(1)

    exit_code = run_pipeline(
        test_dir=test_dir or "",
        runtime_prefix=args.runtime,
        device_name=args.device_type,
        device_udid=args.device_udid,
        create_fresh=not args.reuse_device,
        prepare_only=args.prepare_only,
        skip_prepare=args.skip_prepare,
        skip_build=args.skip_build,
        test_timeout=args.timeout,
        max_infra_retries=args.max_retries,
        diag_dir=args.diag_dir,
        step_timeout=args.step_timeout,
    )
    sys.exit(exit_code)


if __name__ == "__main__":
    main()
