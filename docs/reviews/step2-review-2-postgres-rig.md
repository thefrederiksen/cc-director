# Step 2 Review 2 — PostgreSQL proof rig

Commit reviewed: `73ff92e8d8a3ae1a48975edfd25b1faa2da3964c` only. I used `git diff-tree`/`git show` for the single-commit boundary; I did not compare with `origin/main`.

## Branch-head recheck — partial, stood down before test execution

The original review below was correct for the commit supplied at the time. The Manager later corrected the review scope after two additional commits landed. I fetched and inspected `fd959cac3..origin/nosqlite-stats-w1-pgrig`, whose head was `381d97ed9e6a36bb619dab31adbe9af48b4083c2`. This section supersedes the original findings' status at that head; the original probe record remains below unchanged.

**Current state: incomplete.** The head test project built successfully with zero warnings and zero errors. I recreated the membership, database-owner, gateway-schema-owner, and default-ACL drift states and reran `up`; the script still exited `0` on all four. However, no xUnit drift run obtained the fleet-wide Gateway test lock before this reviewer was stood down.

Run accounting, with no-result runs kept explicitly open:

- A class-filtered clean run timed out after 184 seconds while queued behind another worktree's live lock holder. No test executed. Exit 124 was a command timeout, **not a test result**.
- A retry filtered to `RestrictedRole_MirrorsTheMeasuredHostedGrants` on the membership-drifted rig remained queued and was terminated on the Manager's stand-down instruction before any test executed. It also produced **no result**.
- The database-owner and combined gateway-schema-owner/default-ACL test runs were never started. They remain owed.
- No full local suite was run. All five disposable review containers and volumes were removed at stand-down.

### Head disposition of the original nine findings

| Original finding | Status at `381d97ed9` | Recheck disposition |
|---|---|---|
| 1 — memberships survive `up` | **STANDS; runtime proof-layer answer OPEN** | The script still exits `0` with `memberships=1` and effective `public_CREATE=true`. Static reading shows the new mirror test asserts membership count `0` at `GatewayStatsSchemaPrivilegeProofTests.cs:172-174`, so it should catch this, but the exact filtered run never executed. Severity cannot be reduced on an unexecuted assertion. |
| 2 — ownership survives `up` | **SPLIT; runtime answer OPEN** | The new mirror test statically checks database non-ownership at lines 176-180, so the database-owner arm should go red. It does **not** query the owner of the existing `gateway` schema. The script preserved `gateway_app_r2hschema` as that schema's owner while exiting `0`, and the role could drop it in the original probe. Whether the complete four-test class stays green on this overprivileged shape remains owed. |
| 3 — fail-loud test absent | **RESOLVED; stale-commit artefact** | Commits `64847694c` and `381d97ed9` add the test, proof context, generated migrations, test-project change, and proof report. The test opens the restricted connection at lines 436-440 and checks the listed catalog facts at lines 155-192. |
| 4 — non-container port squatter | **STANDS, high** | The script is unchanged at head. The port scan still sees only Docker containers and readiness still probes container loopback instead of the emitted host endpoint. |
| 5 — default-ACL drift | **STANDS, high; proof-layer answer OPEN** | The head test never queries `pg_default_acl`. In the distinguishable drift fixture, a future `postgres` table contained one row and the restricted role read it (`restricted_read|1`) after `up` exited `0`. The combined full-class run that would establish whether all four tests remain green never started. |
| 6 — arbitrary port on reuse | **STANDS, medium** | The script is unchanged; an existing labelled container still bypasses port-map validation and emits the caller's arbitrary port. |
| 7 — password exposure | **STANDS, medium** | The script is unchanged; successful output, Docker configuration, command arguments, and SQL error output retain the previously proved disclosure paths. |
| 8 — changed superuser password is not applied | **STANDS, medium** | The script is unchanged; reuse still emits a caller-supplied password without applying it to `postgres`. The new tests would turn that into a connection failure rather than a false proof, but `up` itself still exits green. |
| 9 — unchecked non-local Docker context | **STANDS, medium** | The script is unchanged. The added test's `AssertRigShape` also compares the two hosts/ports but does not assert that the host is local (`GatewayStatsSchemaPrivilegeProofTests.cs:95-123`); its `ccpg` database-prefix guard blocks the current Supabase database name but not every non-local database. |

### What is still owed, and by whom

When this reviewer is reseated, recreate the disposable drift rigs and run only the narrow filters below, reading the actual assertion text and numbers:

1. Membership drift: run `RestrictedRole_MirrorsTheMeasuredHostedGrants`; record the membership assertion and explicitly list the later database-owner/database/schema assertions as unexecuted if the membership assertion fails first.
2. Database-owner drift: run the same mirror test; record the owner assertion and explicitly list the later database/schema assertions as unexecuted if it fails there.
3. Gateway-schema-owner plus default-ACL drift: run the complete four-test class. This is the decisive blind-spot probe because neither condition is queried by the mirror method. A green class would leave an overprivileged role undetected.
4. Update the severities only after those results exist. A wait, timeout, exit 99, killed run, or unstarted run closes none of these obligations.

### Ledger and fixture-route assessment

I do **not** currently support row 1 of `docs/step2-proof-ledger.md` as fully **CLOSED**. The capability arm has evidence, and a clean direct catalog probe in the original review showed the intended role shape. The permanent exact-mirror detector arm is at best **PARTIAL** until the schema-owner/default-ACL blind spots are resolved or explicitly bounded by completed evidence. The membership and database-owner assertions exist, but their requested drift runs produced no result before stand-down.

Fixture route: this privilege proof uses a purpose-built `StatsSchemaProofDbContext` and two generated PostgreSQL migrations from that same proof model. That route is appropriate for the narrow question “can this role create a schema, tables, and migration history,” but it is **not** a schema proof for the real sixteen-table statistics model or the existing version-5 SQLite shape. The branch documentation states that limitation correctly.

## Original review of commit `73ff92e8d`

Verdict: **reject as a proof foundation**. The clean first run creates a role matching the listed hosted-role facts, but reused rigs can remain materially overprivileged while `up` exits zero, and no committed test or assertion turns the printed mismatches into failures. I found **9 findings**: 2 critical, 3 high, and 4 medium.

Evidence labels used below:

- **PROVED BY RUNNING** — reproduced with Docker/PowerShell/catalog queries against disposable instances created for this review.
- **INFERRED BY READING** — supported by the commit's control flow but not exercised against external infrastructure.

## Findings

### 1. Critical — `up` preserves extra role memberships and still succeeds

**Evidence: PROVED BY RUNNING**

**Location:** `scripts/pg-stats-proof-rig.ps1:296-320`, `scripts/pg-stats-proof-rig.ps1:334-337`, `scripts/pg-stats-proof-rig.ps1:351-381`

The existing-role path changes role attributes but never removes memberships. The two explicit `REVOKE CREATE` statements only revoke grants from `PUBLIC` and the login itself; they cannot remove a privilege inherited through another role. `Show-Status` prints the membership count and effective privilege but never compares them with the required values or throws.

I created a no-login group role, granted it `CREATE` on `public`, granted that role to `gateway_app_r2drift`, and reran `up`. The rig printed all of the following and exited `0`:

```text
Provisioned. Measured grants on 'ccpgstats_r2drift':
  memberships=1
  public_CREATE=true
rig_exit=0
```

A real password-authenticated connection as `gateway_app_r2drift` then successfully executed `CREATE TABLE public.membership_escape(...)`. The hosted role has zero memberships and cannot create in `public`; this is exactly the overprivileged, falsely green mirror the rig says it prevents.

### 2. Critical — existing database and schema ownership are not normalized or rejected

**Evidence: PROVED BY RUNNING**

**Location:** `scripts/pg-stats-proof-rig.ps1:280-288`, `scripts/pg-stats-proof-rig.ps1:318-326`, `scripts/pg-stats-proof-rig.ps1:334-337`, `scripts/pg-stats-proof-rig.ps1:369-380`

The database is assigned to `postgres` only when first created. On reuse, the code merely observes that it exists. Likewise, `CREATE SCHEMA IF NOT EXISTS gateway AUTHORIZATION postgres` does not repair the owner of an existing `gateway` schema. The database-owner state is printed, not asserted, and the gateway-schema owner is not even printed.

I changed the statistics database owner to the restricted role and reran `up`. It exited `0` while printing:

```text
is_database_owner=true
public_CREATE=true
```

The restricted login then created a table in `public`. PostgreSQL's `public` schema is owned by `pg_database_owner`, so making the login the database owner gives it owner powers that explicit revokes cannot remove.

Separately, I changed the existing `gateway` schema owner to the restricted role and reran `up`. The rig again exited `0`; an independent catalog query showed `gateway_owner=gateway_app_r2hostport2`, and that restricted login successfully dropped the whole `gateway` schema. Both ownership states are more privileged than the hosted role.

### 3. High — the claimed fail-loud test does not exist, and the rig itself never connects as the restricted role

**Evidence: PROVED BY RUNNING**

**Location:** `scripts/pg-stats-proof-rig.ps1:41-45`, `scripts/pg-stats-proof-rig.ps1:149-160`, `scripts/pg-stats-proof-rig.ps1:231-235`, `scripts/pg-stats-proof-rig.ps1:351-381`

The script states that `GatewayStatsSchemaPrivilegeProofTests` asserts the mirror so drift fails loud. `git diff-tree -r 73ff92e8d` shows that the commit adds only `scripts/pg-stats-proof-rig.ps1`; it contains no test-project change. A repository search found no `GatewayStatsSchemaPrivilegeProofTests` class and no consumer of `CC_GATEWAY_TEST_PG_STATS_CONNECTION`.

Every SQL call made by the committed rig selects `-U postgres`; `Wait-ForPostgres` does the same. `Show-Status` only emits catalog text. Findings 1 and 2 demonstrate that even values the status output visibly identifies as wrong do not affect the exit code. Therefore a restricted password, connection route, migration, or privilege boundary can be wrong while the rig's own checks remain green. The manual restricted-login probes in this review are evidence the commit itself does not supply.

### 4. High — a non-container host-port squatter is invisible, and readiness checks the wrong endpoint

**Evidence: PROVED BY RUNNING**

**Location:** `scripts/pg-stats-proof-rig.ps1:203-213`, `scripts/pg-stats-proof-rig.ps1:216-240`, `scripts/pg-stats-proof-rig.ps1:243-249`

`Assert-PortIsFree` inspects only `docker ps`. `Wait-ForPostgres` then connects from inside the container to container loopback; it never probes the published `localhost:$Port` endpoint that is handed to tests.

I bound a non-container .NET `TcpListener` to `0.0.0.0:56444`, verified it was listening, and ran `up` for a new instance on port 56444. On this Docker Desktop host the container publication coexisted with the listener, the rig provisioned successfully, printed its environment strings, and exited `0`. A subsequent host connection to `127.0.0.1:56444` was accepted by the squatter and received its `squatter` marker, not PostgreSQL. Thus the mandatory port and readiness check do not establish that tests will connect to this rig at all.

### 5. High — default-privilege drift survives `up` and is absent from status

**Evidence: PROVED BY RUNNING**

**Location:** `scripts/pg-stats-proof-rig.ps1:295-329`, `scripts/pg-stats-proof-rig.ps1:358-375`

The clean container had no `pg_default_acl` rows for the restricted role, but `up` neither clears nor validates default ACLs on reuse. `Show-Status` does not query them.

I added a default privilege granting the restricted role `SELECT` on future tables created by `postgres` in `gateway`, then reran `up`. It printed the normal-looking status and exited `0`. A table subsequently created by `postgres` had the drifted grant, and a real restricted connection read it successfully (`restricted_read|1`). The local role's effective access can therefore differ from the hosted role without any visible status field or failure.

### 6. Medium — an existing labelled container accepts an arbitrary `-Port` and emits a false endpoint

**Evidence: PROVED BY RUNNING**

**Location:** `scripts/pg-stats-proof-rig.ps1:243-249`, `scripts/pg-stats-proof-rig.ps1:251-276`

The existing-container branch skips `Assert-PortIsFree` and never compares `-Port` with the container's published mapping. Provisioning and readiness use `docker exec`, so they do not use the supplied port either.

For a container actually published on 56443, I reran `up -Port 59999`. The rig exited `0` and emitted both connection strings with `Port=59999`. A stale or mistaken port can therefore redirect or break every downstream proof while the rig declares itself ready.

### 7. Medium — passwords are deliberately exposed to logs/environment, and error handling repeats them

**Evidence: PROVED BY RUNNING (process-list exposure additionally inferred from the command-line parameter design)**

**Location:** `scripts/pg-stats-proof-rig.ps1:91-93`, `scripts/pg-stats-proof-rig.ps1:134-160`, `scripts/pg-stats-proof-rig.ps1:265-269`, `scripts/pg-stats-proof-rig.ps1:307-308`, `scripts/pg-stats-proof-rig.ps1:334-337`, `scripts/pg-stats-proof-rig.ps1:384-395`

Every successful `up` prints both plaintext passwords to stdout via `Write-EnvLines`, which directly reaches captured test logs. The superuser password is also stored as `POSTGRES_PASSWORD` in the container configuration and was retrievable with `docker inspect`. Both passwords are accepted as process command-line arguments.

Failure paths amplify the leak: Docker errors join and print the complete argument vector (including `POSTGRES_PASSWORD=...`), while SQL errors print the full provisioning SQL containing `PASSWORD '$RestrictedPassword'`. I passed the harmless marker `audit'quote`; the apostrophe also proved that the password is not SQL-escaped, and the thrown error repeated the marker and full SQL twice. The PowerShell/bash environment assignments likewise place unescaped passwords inside single quotes.

### 8. Medium — changing `-SuperuserPassword` on reuse produces a green rig with an invalid connection string

**Evidence: PROVED BY RUNNING**

**Location:** `scripts/pg-stats-proof-rig.ps1:231-245`, `scripts/pg-stats-proof-rig.ps1:251-276`

`POSTGRES_PASSWORD` is used only during `docker run`. On an existing container, a newly supplied `-SuperuserPassword` is not applied to the `postgres` role, but `Get-SuperuserConnection` emits it anyway. The internal readiness query succeeds through the image's loopback `trust` rules and therefore cannot detect the bad password.

I reran an existing instance with the marker `audit-super-new`. The rig exited `0` and printed a superuser connection containing that password. Password-authenticated access through the published port rejected the marker; the original `proof` password still succeeded, and `docker inspect` still showed `POSTGRES_PASSWORD=proof`. This also supplies a concrete case where substituting a false connection secret leaves all rig checks green.

### 9. Medium — “local Docker only” is an unchecked Docker-context assumption

**Evidence: INFERRED BY READING**

**Location:** `scripts/pg-stats-proof-rig.ps1:134-141`, `scripts/pg-stats-proof-rig.ps1:155-160`, `scripts/pg-stats-proof-rig.ps1:265-273`, `scripts/pg-stats-proof-rig.ps1:340-348`

All lifecycle and SQL operations invoke the ambient `docker` CLI without checking the active context or `DOCKER_HOST`. With a remote Docker context, `run`, `exec`, `start`, and `rm -f -v` operate on a non-local machine and its PostgreSQL container despite the absolute “local Docker only” claim.

I found no parameter that can set the `psql` host directly to Supabase: SQL is hardcoded to loopback inside the selected container, emitted test strings use `localhost`, and database/role names carry the mandatory instance suffix. That is a useful barrier against directly supplying the hosted Supabase endpoint, but it does not make the broader non-local claim true or protect a selected remote Docker daemon.

## Probes that passed

- A clean `up` produced a real password-authenticated restricted session with `session_user=current_user=gateway_app_r2audit`. Independent catalog queries showed: no superuser/CREATEDB/CREATEROLE/BYPASSRLS/replication, `INHERIT` and `LOGIN`, zero memberships, database owner `postgres`, gateway owner `postgres`, public owner `pg_database_owner`, no owned schemas before migration, no restricted-role default-ACL rows, database `CREATE`, gateway `USAGE+CREATE`, public `USAGE` without `CREATE`, and the requested search path.
- As the restricted login, `CREATE SCHEMA gateway_stats`, a table there, and a table in `gateway` succeeded; creating in `public`, creating a database, and creating a role failed with permission errors.
- Missing `-Instance` and missing `-Port` failed. Slash, quote, space, underscore, and a 21-character instance failed validation. A valid 20-character instance was accepted; at that bound none of the derived PostgreSQL identifiers approach the 63-byte truncation limit, and the hyphen-to-underscore transform is injective over the allowed instance alphabet.
- A same-name running container without the instance label was refused before adoption. A port published by another Docker container was refused and the squatter was named.
- Revoking database `CREATE` in one instance changed only that instance; a second instance remained granted. Grant restored only the targeted instance.
- All disposable review containers and volumes were removed after the probes; the pre-existing worker containers were left running.
