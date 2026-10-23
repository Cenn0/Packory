#!/usr/bin/env python3
"""Validate fully expanded opt-in Compose profiles without echoing secrets or paths.

This is a structural contract check only. It cannot certify TLS, bind-mount
permissions, host networking, client interoperability or process recovery.
"""
import json
import sys


def require(condition, message):
    if not condition:
        raise ValueError(message)


def check(profile, spec):
    require(type(spec) is dict, "Expected a Compose config object")
    services = spec.get("services")
    require(type(services) is dict and set(services) == {"packory", "skillspector"},
            "Unexpected deployment services")
    app = services["packory"]
    worker = services["skillspector"]

    require("privileged" not in app or app["privileged"] is False, "Privileged app forbidden")
    require("privileged" not in worker or worker["privileged"] is False, "Privileged worker forbidden")
    for name, service in (("application", app), ("worker", worker)):
        require("ALL" in service.get("cap_drop", []), name + " must drop all capabilities")
        require("no-new-privileges:true" in service.get("security_opt", []),
                name + " must disable new privileges")
    require(worker.get("network_mode") == "none", "Worker must be network-isolated")
    require(worker.get("read_only") is True, "Worker root filesystem must be read-only")
    require(worker.get("user") == "1654:1654", "Worker must keep its non-root UID/GID")
    require(not worker.get("ports"), "Worker must never expose ports")
    require(not worker.get("networks"), "Worker cannot attach to ingress")
    require(worker.get("pids_limit") is not None, "Worker process cap is required")
    require(worker.get("mem_limit") is not None or
            worker.get("deploy", {}).get("resources", {}).get("limits", {}).get("memory") is not None,
            "Worker memory cap is required")

    appenv = app.get("environment", {})
    require(type(appenv) is dict and appenv.get("PACKORY_ANONYMOUS_READ") == "false",
            "Anonymous global reads must remain disabled")
    for identity in ("ADMIN_TOKEN", "READ_TOKEN", "WRITE_TOKEN"):
        key = "PACKORY_" + identity
        secret = "packory_" + identity.lower()
        require(key not in appenv, "Direct app secret environment setting forbidden")
        require(appenv.get(key + "_FILE") == "/run/secrets/" + secret,
                "App must consume a mounted secret file")
        require(secret in spec.get("secrets", {}), "Required file-backed secret absent")
        require(isinstance(spec["secrets"][secret].get("file"), str),
                "Secret source must be an operator-selected file")
    require("ALL" in app.get("cap_drop", []), "Application capabilities were increased")
    require(not app.get("pid"), "Application cannot join a host PID namespace")

    if profile == "loopback":
        ports = app.get("ports", [])
        require(type(ports) is list and len(ports) == 1, "Loopback profile needs exactly one port")
        published = ports[0]
        require(str(published.get("target")) == "8080" and
                str(published.get("published")) == "8080" and
                published.get("host_ip") == "127.0.0.1",
                "Loopback profile must bind port 8080 exclusively to 127.0.0.1")
    elif profile == "private-ingress":
        require(not app.get("ports"), "Private ingress must never publish app host ports")
        networks = app.get("networks", {})
        require(type(networks) is dict and set(networks) == {"ingress"},
                "Application must attach only to dedicated ingress")
        network = spec.get("networks", {}).get("ingress", {})
        require(network.get("external") is True,
                "Ingress network must be operator-owned and external")
        require(appenv.get("PACKORY_PUBLIC_BASE_URL", "").startswith("https://"),
                "Private ingress needs HTTPS public origin")
        require(appenv.get("PACKORY_OCI_AUTHENTICATED_PING") == "true",
                "Private OCI ping must require authentication")
    else:
        raise ValueError("Unknown Compose acceptance profile")


def main():
    if len(sys.argv) != 2 or sys.argv[1] not in ("loopback", "private-ingress"):
        print("Specify the configured Compose profile only.", file=sys.stderr)
        return 2
    try:
        raw = sys.stdin.read(256 * 1024 + 1)
        require(len(raw) <= 256 * 1024, "Expanded Compose config exceeds 256 KiB")
        spec = json.loads(raw)
        check(sys.argv[1], spec)
    except (ValueError, TypeError, KeyError):
        # Never echo expanded configuration; it includes local host file paths.
        print("Compose profile violates an isolation or authorization invariant.", file=sys.stderr)
        return 1
    print("Compose structure accepted; runtime and TLS qualification still required.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
