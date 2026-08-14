# QuePaxa: choosing the register, and operating its membership

Verisync ships two consensus registers over a single value: `QuePaxaVersionedRegister<TValue>` and the Fast
CASPaxos register. This is the choice between them, and the procedures for a QuePaxa register whose membership
changes while it serves.

## 1. Choosing between the two registers

### Semantics decide before latency does

CASPaxos applies the caller's change function inside the round — the round recovers a value, the change is
applied to that value, and the result is accepted — so intent survives contention. QuePaxa decides among
whole proposed values, so a proposer that loses has its proposal discarded and must re-read and re-propose:
`WriteAsync` is that retry loop, adopting the winner's record and recomputing the update against it, outside
the round. The two are interchangeable only for idempotent, monotone or explicitly abort-on-lose updates;
linearizable read-modify-write inside the round remains the CASPaxos registers' claim.

### Quorum arithmetic

| replicas | QuePaxa majority | Fast CASPaxos fast quorum | consequence |
|---|---|---|---|
| 3 | 2 of 3 | 3 of 3 | the fast quorum is unanimous and tolerates no failure |
| 4 | 3 of 4 | 3 of 4 | the quorums coincide and the contrast vanishes |
| 5 | 3 of 5 | 4 of 5 | QuePaxa's fast path survives one failure; the fast quorum then needs every survivor |
| 7 | 4 of 7 | 6 of 7 | the fast quorum reaches the second-farthest replica |

QuePaxa's per-instance leader — derived from the previous version's writer, never elected — commits in one
round trip at the majority radius; a non-leader pays three such trips. Fast CASPaxos commits in one round
trip from any replica when uncontended but at the fast-quorum radius, falling back under contention to a
classic recovery round at the majority radius. The argument is about quorum radius, not link length.

### What was measured

A discrete-event simulation on a virtual clock, not a production benchmark: four replica counts by five
topology tiers by four writer counts, 80 cells, each swept at three arrival spreads over 2000 trials per
configuration row and re-measured at a second, disjoint seed base. Inter-region delays come from published
median profiles of one cloud provider halved to one way; the co-located and availability-zone delays are
modelling choices a real deployment's processing cost is comparable with, so the signal at those two tiers is
round structure rather than latency. A verdict is the argmin of the representative writer's p95 commit
latency, with a ten-percent band inside which the cell is published as "either" rather than as a winner. No
cell exercises a failed replica.

### Verdicts for mergeable update shapes

| tier | QuePaxa outright | Fast CASPaxos outright | either, QuePaxa preferred | either, Fast preferred | void |
|---|---|---|---|---|---|
| co-located | 8 | 4 | 24 | 12 | 0 |
| multi-az | 16 | 1 | 19 | 12 | 0 |
| multi-region | 32 | 0 | 16 | 0 | 0 |
| global | 37 | 0 | 10 | 1 | 0 |
| clustered-majority | 13 | 0 | 35 | 0 | 0 |
| all tiers | 106 | 5 | 104 | 25 | 0 |

QuePaxa is named in 210 of the 240 verdicts and Fast CASPaxos in 30, every one of the 30 at the co-located
tier, the multi-availability-zone tier, or one global cell; the five outright Fast CASPaxos wins are all at
two writers and the widest arrival spread.

### Read-modify-write

A second measurement ran a genuine read-modify-write workload over 70 of the 80 cells — the missing ten have
more writers than replicas, which cannot exist because a non-member's write reports `OutsideConfiguration`
without proposing — as 140 cell runs and 2940 measured rows, over which an oracle reading the replicas' own
values rejected no trial. Both its readings are given here, because which governs is a fact about the
workload rather than about either protocol: the inert reading is the raw argmin, which is what an idempotent,
monotone or abort-on-lose shape gets, and the retry-ceiling reading removes any QuePaxa configuration whose
measured conflict rate is above 0.10, which is what a non-mergeable shape imposes once retries are budgeted.

A QuePaxa read-modify-write conflicts whenever another writer's instance closes the version first, so under
simultaneous arrival exactly one writer of W wins and the rate sits at (W-1)/W. The 120 one-writer rows read
0.000; across the 1200 multi-writer rows the rate never falls below 0.411 and reaches 0.859, so not one sits
at or below 0.10 and the ceiling removes every QuePaxa configuration in every multi-writer cell. Of 420
verdicts pooled over both seed bases:

| inert reading names | retry-ceiling reading names | verdicts |
|---|---|---|
| QuePaxa | Fast CASPaxos | 181 |
| QuePaxa | void | 1 |
| QuePaxa | QuePaxa | 72 |
| Fast CASPaxos | Fast CASPaxos | 166 |

QuePaxa goes from 254 verdicts to 72 and Fast CASPaxos from 166 to 347, and the 72 survivors are exactly the
one-writer verdicts, 36 at each base: under the retry-ceiling reading no multi-writer verdict names QuePaxa
anywhere. Under a retry ceiling the difference is therefore a protocol switch and not a tuning knob. Raw
latency often still favours QuePaxa even so, which is why both readings are published rather than one. The
caveat runs the other way as well: of the 2940 rows, 389 carry a write that never committed inside its
attempt budget, 381 of them Fast CASPaxos rows and 8 QuePaxa rows.

### Choosing

- Idempotent, monotone or abort-on-lose updates on a placement wider than one availability zone: QuePaxa,
  named in every multi-region and every clustered-majority verdict.
- Contended, non-mergeable read-modify-write on one key: Fast CASPaxos, or move that state onto a replicated
  data type. A consensus register holds the state that must be agreed, not the state that is hot.
- One writer per key: either, since the 120 one-writer verdicts are identical under both readings.
- Co-located and uncontended, or four replicas where the quorums coincide: either, on semantics alone.

## 2. Operating a QuePaxa register's membership

### Genesis, chain identity, and the derived membership

A membership is a field of the committed record and not configuration the hosts are told: the membership
governing one version is the next membership named by the record at the version before it, so every host
holding that record derives the same recorder set, quorum, hedging order and leader without exchanging a
message. Genesis is the base case — `QuePaxaConfiguration.CreateGenesis` mints the chain identity as an
order-sensitive digest of the founding member list, carried unchanged by every later membership, and two
hosts given different genesis lists mint different identities and decline each other forever. A change is an
ordinary write whose record names a different membership, decided entirely under the membership that existed
before it while the new one governs the version after, so no joint consensus is needed. `ReconfigureAsync` is
that path, and it refuses before a chain's first write because a change carries the committed value forward
and there is none: bootstrap by writing once under genesis.

### What a host wires

Each host runs a `QuePaxaVersionedNode<TValue>` over the genesis membership and its own replica id, driven by
a `QuePaxaVersionedRunner<TValue>` whose loop is given a persist delegate. A register also requires a recorder
endpoint resolver, a priority source, a clock and an attempts-per-recorder bound. Four seams are optional.

| optional seam | what is lost by omitting it |
|---|---|
| committed-version observer | a delayed writer cannot stand down on a version already closed, so every scheduled writer activates on its delay |
| per-member record reader | `ReadAsync` reports only what this replica already holds and cannot catch up |
| committed-record publisher | decided records are retained but never disseminated, so the next version stays unservable until a host learns the current one by another route |
| per-member version query | `ReadReadinessAsync` refuses rather than reporting nothing, because a report of nothing is also what a silent cluster produces |

The endpoint map from member id to address is the deployment's. The register resolves each member separately
and builds its endpoint array in the membership's own order, and a member the map cannot resolve keeps its
slot: a quorum is counted over the slots built, so dropping one would shrink the majority, not reachability.

### Admitting a member

1. Mint a fresh replica id, provision the host with the same genesis membership and an empty durable store,
   and start it. It holds no record and is not yet a member.
2. Update the endpoint map so every member resolves the joiner and the joiner resolves everyone, before the
   change — otherwise the joiner is a member of the quorum arithmetic and of nothing else.
3. Read readiness over the membership the change would install. `ReadReadinessAsync` takes a membership, so
   the incoming side is observable while no register yet runs under it.
4. Call `ReconfigureAsync` with the delta "with this member". The write runs under the outgoing membership,
   so the joiner neither counts toward the quorum nor needs to be reachable for it to commit.
5. The joiner learns the installing record by its own catch-up read and by the writer's push, which at a
   membership boundary is addressed to the union of the deciding and the installed memberships.
6. Read readiness again, and gate the next step on a quorum of the new membership having learned that record.
   Until it holds, fault tolerance has decreased, because the quorum rose while the joiner was cold.

Grow one member at a time. Slack at a boundary is the intersection's size minus the new quorum: zero for
three to four, zero for three to five in one step with a further fault wedging progress, and (*n*-3)/2 from
odd *n* to *n*+1.

### Retiring a member

1. Stop directing client writes at the host. Draining is a client-routing act the protocol has no notion of.
2. Call `ReconfigureAsync` with the delta "without that member". The write runs under the membership that
   still includes it, so the retiring host may be counted in the quorum that removes it.
3. The retired host learns the installing record through the boundary push, computes that it is not in its
   own active membership, and declines every later record request. Its own writes then report
   `OutsideConfiguration`, spending no attempt and throwing nothing.
4. The gate: confirm through a readiness report that a quorum of the new membership, not counting the host
   being retired, has learned that record. Only then stop the host or destroy its store.

The gate is safety and not hygiene. If the host is stopped first and the remaining holders of that record
then crash, no live host serves the next instance and none can prove what the record was; there is no
documented recovery. A change to a membership disjoint from the current one is the extreme case, every holder
of the installing record being an outgoing host. Shrinking is otherwise benign: no cold members, and the
quorum falls. There is no minimum size — the library refuses only the empty membership and duplicate members,
the latter because a member listed twice would answer twice and count twice toward a quorum. A leader that
removes itself costs one leaderless instance, which is one extra round.

### Reading a readiness report

`RegisterReadiness` names every member of the membership it was measured over, in that membership's order,
with the version each one answered. Reachability is counted beside the versions because the two fail
differently: a membership answering an old version is behind, one that does not answer is unavailable, and an
operator waiting out the first would wait forever on the second. An unreachable member reports no version at
all rather than reporting that it has learned nothing, and `QuorumHasLearned` asks the quorum question of the
report rather than leaving it to be inferred. A report is separate answers and not a consistent cut, which is
what a gate needs: a host that has learned a version does not unlearn it.

Each answer is a `MemberVersionReport` naming the host that produced it, and the register refuses one naming
a member other than the one it asked: that is a wiring error in the endpoint map — two routes landing on one
host would let one replica fill two slots and clear a decommission gate on fewer distinct replicas than it
claims — and never a fault of the member. The identity is the host's own claim rather than authentication.

### Restart and rejoin

A host restarts from its own durable store, restoring the node from the genesis membership, its own replica
id and the stored state. A torn snapshot refuses to start; a stale but internally consistent one starts and
serves an old version. Two restore checks are membership checks: the stored active membership must agree with
the one derived from the stored record, and the stored membership must name the chain the host's genesis
names. After an outage the live membership is whatever the highest surviving record says, so the operator's
job is to find that record and get it to a quorum of the membership it names. A replica removed by mistake
rejoins by being admitted again, which is not a rollback: it joins an instance that did not exist while it
was out.

### Rules an operator can break from outside

| act | consequence |
|---|---|
| reuse a replica id across a wipe | undetectable by any host: the machine answers below the step its identity already answered from. An empty store means a new identity, and ids carry no address, so re-addressing is free |
| decommission before a quorum of the new membership holds the installing record | the register can be wedged, with no documented recovery |
| start two hosts from different genesis lists | different chain identities, declining each other forever; the chain never commits its first version |
| express a change as an absolute set instead of a delta | a change re-applied after losing its instance reinstates whatever the concurrent winner removed. `With` and `Without` are idempotent so that the retry is safe |
| change to a membership the caller cannot reach | safe and immediately unavailable. The library cannot evaluate reachability and does not refuse it, so the pre-flight readiness read is the operator's |
