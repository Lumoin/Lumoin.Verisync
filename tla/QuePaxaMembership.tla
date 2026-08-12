-------------------------- MODULE QuePaxaMembership --------------------------
(*
Dynamic membership for the QuePaxa versioned register: the configuration is a
field of the decided record, and the configuration carried by the record decided
at version v governs consensus instance v+1 and no other instance.

This module sits at a higher abstraction than QuePaxaConcrete and answers a
different question. QuePaxaConcrete asks whether one consensus instance is safe
under a fixed recorder set, and its largest configuration already holds
27,328,647 distinct states. The question here is whether every quorum that ever
completes lies inside one configuration, and whether two hosts serving one
instance can ever derive different ones. Composing the two would multiply the
alphabets for no new property, so one instance's protocol is collapsed into a
single atomic Decide guarded by a quorum of that instance's configuration.

The composition between the two modules is a named predicate rather than a
checked refinement. QuePaxaConcrete assumes the recorders of one instance run
under one configuration, and OneConfigurationPerInstance is exactly that
assumption discharged as a checked invariant here. That composition is argued
in the same register as the existing QuePaxaConcrete onto QuePaxaAbstract
argument, and it is exact for the leader hypothesis while it holds for the
recorder set only because QuePaxaConcrete carries a single Recorders constant.

Decide carries one guard that is not a deployment rule but the collapsed-level
encoding of intra-instance agreement: a host counted in a decision at a version
is not counted again at that version for a different record. Without it two
quorums of one uniform configuration could both decide and the positive
configurations would be red, so the whole matrix would pin nothing. With it, two
different records at one version need disjoint quorums, which majorities of one
member set cannot supply, while a non-uniform genesis affords genuinely disjoint
quorums across the forked configurations. That is the witness class the panel
settled on: a shared recorder between two quorums is not a fork, because in
QuePaxa the shared recorder's aggregate carries the decided proposal forward.

A decision installs the record at its writer alone. The recorders counted in a
quorum have recorded rather than learned, since dissemination is best effort and
carries no result, and Learn is what moves a record to any other host. That
asymmetry is the model's statement of the mechanism that preserves a decision:
agreement within the instance over its one fixed recorder set, and not the
version gate, which is the activation boundary rather than the preservation
rule.

Five deployment rules are constants in the shipped mould, so the model is what
says each is load-bearing rather than tidy: ConfigDerivedFromRecord,
VersionGateBinds, RepliesOnlyFromMembers, ClusterBinds and DecommissionGated. A
sixth, ProposerMustBeMember, is the one whose withdrawal is expected to stay
green, which is what prices the removed-proposer filter honestly as operability
rather than safety.

What this module does not do. It does not model the intra-instance protocol, the
priorities, the steps or the fast path, which are QuePaxaConcrete's subject. It
does not model durability or torn writes, whose restore rules stay with the unit
suite on the precedent this directory already records. It checks no progress
property: Learn is enabled from any live holder, so dissemination is
over-approximated, and the only thing said about availability is
LatestDecisionSurvives, which is a state predicate about whether recovery
remains possible rather than about whether it happens.

Versions are bounded by MaxVersion, which is what makes the state space finite.
Every configuration therefore reaches a terminal state once the chain is spent,
so deadlock is not checked anywhere in this matrix - the terminal state is the
bound rather than a defect. The QuePaxa positives elsewhere in this directory do
check it, and can, because their rounds do not run out; the two Raft matrices,
which are bounded the same way this one is, do not.

Records carry no value field. Agreement needs two records at one version to be
distinguishable and the writer already distinguishes them; no invariant and no
guard in this module reads a value, so a value would be a dimension that only
multiplies the state space.
*)
EXTENDS Naturals, FiniteSets

CONSTANTS
    Hosts,                    \* Every identity that can hold state or be counted.
    Configs,                  \* The configuration identifiers.
    Clusters,                 \* The chain identities a configuration can carry.
    Members,                  \* Members[c], the member set of configuration c.
    Cluster,                  \* Cluster[c], the chain identity configuration c carries.
    Genesis,                  \* Genesis[h], the configuration host h was provisioned with.
    LocalConfig,              \* LocalConfig[h], the configuration h reads when the derivation is withdrawn.
    MaxVersion,               \* The highest version the register can reach.
    ConfigDerivedFromRecord,  \* Rule: the active configuration is a function of the held record.
    VersionGateBinds,         \* Rule: a host serves only the version after the one it holds.
    RepliesOnlyFromMembers,   \* Rule: a decision counts only hosts inside the instance's configuration.
    ClusterBinds,             \* Rule: a host declines a record carrying another chain identity.
    DecommissionGated,        \* Rule: a host is retired only once the incoming configuration holds the newest record.
    ProposerMustBeMember,     \* Rule: only a member of the instance's configuration may propose.
    NoRecord                  \* The sentinel for a host that holds no committed record.

Versions == 1..MaxVersion

\* A record names the version it settles, the replica that wrote it and the
\* configuration that governs the next instance. The configuration is inside the
\* decided value for the same reason the writer is: the next instance's
\* derivation must read an agreed fact, and the decided value is the only agreed
\* thing an instance produces.
Records == [version: Versions, writer: Hosts, config: Configs]

ASSUME Hosts # {}
ASSUME Configs # {}
ASSUME Members \in [Configs -> SUBSET Hosts]
ASSUME \A c \in Configs : Members[c] # {}
ASSUME Cluster \in [Configs -> Clusters]
ASSUME Genesis \in [Hosts -> Configs]
ASSUME LocalConfig \in [Hosts -> Configs]
ASSUME MaxVersion \in Nat /\ MaxVersion > 0
ASSUME ConfigDerivedFromRecord \in BOOLEAN
ASSUME VersionGateBinds \in BOOLEAN
ASSUME RepliesOnlyFromMembers \in BOOLEAN
ASSUME ClusterBinds \in BOOLEAN
ASSUME DecommissionGated \in BOOLEAN
ASSUME ProposerMustBeMember \in BOOLEAN
ASSUME NoRecord \notin Records

VARIABLES
    held,       \* held[h], the committed record host h holds, or NoRecord.
    alive,      \* alive[h], whether host h is still running.
    decisions,  \* decisions[k], the records decided at version k. A set, so two are representable.
    counted     \* counted[k], ghost: one entry per decision, carrying what that decision counted.

vars == <<held, alive, decisions, counted>>

\* The quorum of a configuration is a strict majority of its members, which is
\* what the shipped proposer computes from the length of its endpoint array.
Quorum(c) == (Cardinality(Members[c]) \div 2) + 1

\* The configuration a host is serving under. With the derivation in force it is
\* a function of the held record, which is the whole design; with the derivation
\* withdrawn the host reads its own deployment file instead. Genesis governs the
\* first instance either way, so the divergence the withdrawal models appears
\* only once a record exists to disagree with.
ActiveConfigOf(h) ==
    IF held[h] = NoRecord
    THEN Genesis[h]
    ELSE IF ConfigDerivedFromRecord THEN held[h].config ELSE LocalConfig[h]

\* The one instance a host serves. A host holding a record at k-1 serves k and
\* nothing else, which is the version gate the shipped host already enforces.
LiveVersionOf(h) == IF held[h] = NoRecord THEN 1 ELSE held[h].version + 1

\* The chain a host belongs to, which is the chain identity of the configuration
\* it is serving under.
ClusterOf(h) == Cluster[ActiveConfigOf(h)]

\* Whether a recorder answers this request at all. The version arm is the gate;
\* its withdrawal lets a host serve any instance at or above the one it has
\* caught up to, which is what a host that answers from a window does. The
\* cluster arm is the genesis defence, and it is the same predicate the shipped
\* host consults at Handle and again at Learn.
Serves(h, k, r) ==
    /\ alive[h]
    /\ IF VersionGateBinds THEN LiveVersionOf(h) = k ELSE LiveVersionOf(h) =< k
    /\ (ClusterBinds => Cluster[r.config] = ClusterOf(h))

\* Whether this host has already been counted at this version for some other
\* record. This is the binding guard, and the module header says why it is here.
BoundAtOtherRecord(k, r, h) ==
    \E e \in counted[k] : h \in e.quorum /\ e.record # r

DecidedVersions == {k \in Versions : decisions[k] # {}}

HighestDecided ==
    IF DecidedVersions = {}
    THEN 0
    ELSE CHOOSE k \in DecidedVersions : \A j \in DecidedVersions : j =< k


Init ==
    /\ held = [h \in Hosts |-> NoRecord]
    /\ alive = [h \in Hosts |-> TRUE]
    /\ decisions = [k \in Versions |-> {}]
    /\ counted = [k \in Versions |-> {}]


\* A version settles. The proposer resolves the configuration from its own held
\* record and counts a majority of that configuration's endpoint array, which is
\* why the counted set has exactly the quorum's cardinality rather than at least
\* it: the array has one slot per member, so a stranger occupies a slot rather
\* than adding one.
\*
\* The record installs at the writer alone. Everyone else learns it, or does not.
Decide(k, w, nc, Q) ==
    /\ alive[w]
    /\ LiveVersionOf(w) = k
    /\ LET c == ActiveConfigOf(w)
           r == [version |-> k, writer |-> w, config |-> nc]
       IN
        /\ (ProposerMustBeMember => w \in Members[c])
        \* The chain identity is carried forward by every configuration change,
        \* so a record can never move its own chain to another one.
        /\ Cluster[nc] = Cluster[c]
        /\ r \notin decisions[k]
        /\ Cardinality(Q) = Quorum(c)
        /\ (RepliesOnlyFromMembers => Q \subseteq Members[c])
        /\ \A h \in Q : Serves(h, k, r)
        /\ \A h \in Q : ~BoundAtOtherRecord(k, r, h)
        /\ decisions' = [decisions EXCEPT ![k] = @ \union {r}]
        \* The ghost records what this decision counted. The two booleans are
        \* facts about the moment of counting rather than about the state that
        \* follows it, and a state predicate cannot recover them afterwards
        \* because held moves on; this is the reason QuePaxaDurable keeps served.
        /\ counted' = [counted EXCEPT ![k] = @ \union
            {[record     |-> r,
              config     |-> c,
              quorum     |-> Q,
              caughtUp   |-> \A h \in Q : LiveVersionOf(h) = k,
              oneCluster |-> \A g1, g2 \in Q : ClusterOf(g1) = ClusterOf(g2)]}]
        /\ held' = [held EXCEPT ![w] = r]
        /\ UNCHANGED alive


\* A host adopts a strictly newer record from a host that still holds it. The
\* live holder is what makes the decommission hazard reachable: a record whose
\* only holder has been retired can no longer be learned by anyone.
\*
\* The cluster arm is here as well as in Serves, and it is load-bearing rather
\* than symmetric: without it a misprovisioned host adopts the majority chain's
\* record by dissemination and joins that chain, so the identity check would
\* close the fork at the recorder and leave it open at the learn. This is the
\* decision that the check is one predicate consulted at both sites.
Learn(h, g) ==
    /\ h # g
    /\ alive[h]
    /\ alive[g]
    /\ held[g] # NoRecord
    \* The empty hold is an IF rather than a disjunct because TLC forks a
    \* disjunction inside an action and evaluates both arms, so the arm reading
    \* version from a NoRecord hold is reached even when the other arm is true.
    /\ (IF held[h] = NoRecord THEN TRUE ELSE held[g].version > held[h].version)
    /\ (ClusterBinds => Cluster[held[g].config] = ClusterOf(h))
    /\ held' = [held EXCEPT ![h] = held[g]]
    /\ UNCHANGED <<alive, decisions, counted>>


\* Whether retiring this host still leaves the newest decided record inside a
\* quorum of the configuration that record installs. This is the operator gate
\* the runbook states as a numbered rule, and the point of making it a constant
\* is that the model rather than the prose says what its absence costs.
DecommissionSafe(h) ==
    \* The empty chain is an IF rather than a disjunct because this predicate is
    \* consulted inside an action, where TLC forks a disjunction and evaluates
    \* both arms, so the arm applying decisions at zero is reached regardless.
    IF HighestDecided = 0
    THEN TRUE
    ELSE \E r \in decisions[HighestDecided] :
        Cardinality({g \in Members[r.config] : g # h /\ alive[g] /\ held[g] = r})
            >= Quorum(r.config)


Decommission(h) ==
    /\ alive[h]
    /\ (DecommissionGated => DecommissionSafe(h))
    /\ alive' = [alive EXCEPT ![h] = FALSE]
    /\ UNCHANGED <<held, decisions, counted>>


\* The three actions are separate disjuncts so that a coverage run can tell them
\* apart, on the Serve and Refuse precedent in QuePaxaConcrete.
Next ==
    \/ \E k \in Versions, w \in Hosts, nc \in Configs, Q \in SUBSET Hosts : Decide(k, w, nc, Q)
    \/ \E h, g \in Hosts : Learn(h, g)
    \/ \E h \in Hosts : Decommission(h)


Spec == Init /\ [][Next]_vars


DecisionEntries ==
    [record: Records, config: Configs, quorum: SUBSET Hosts,
     caughtUp: BOOLEAN, oneCluster: BOOLEAN]


TypeOK ==
    /\ held \in [Hosts -> Records \union {NoRecord}]
    /\ alive \in [Hosts -> BOOLEAN]
    /\ decisions \in [Versions -> SUBSET Records]
    /\ counted \in [Versions -> SUBSET DecisionEntries]
    \* A held record is one some decision produced, which is what says Learn
    \* never invents a record and the model never installs one behind its back.
    \* The empty hold is an IF for the same evaluation reason as in Learn.
    /\ \A h \in Hosts :
        IF held[h] = NoRecord THEN TRUE ELSE held[h] \in decisions[held[h].version]


\* The money invariant. decisions[k] is a set, so two records at one version are
\* representable and this is a real check rather than a predicate that cannot
\* fail.
Agreement == \A k \in Versions : Cardinality(decisions[k]) =< 1


\* Two hosts serving one instance of one chain derive the same configuration.
\* This is the assumption QuePaxaConcrete makes about its recorders, discharged
\* here as a checked invariant. Hosts in different chains are not compared,
\* because two chains are two registers rather than one register forked; what
\* makes that distinction sound is that a chain identity is the digest of the
\* genesis member array, so two hosts provisioned with different member lists
\* are in different chains by construction.
OneConfigurationPerInstance ==
    \A h1, h2 \in Hosts :
        (/\ LiveVersionOf(h1) = LiveVersionOf(h2)
         /\ ClusterOf(h1) = ClusterOf(h2))
        => ActiveConfigOf(h1) = ActiveConfigOf(h2)


\* Every decision counted only hosts inside the configuration that decision ran
\* under. The configuration is read from the decision's own ghost entry rather
\* than recomputed, which is what keeps this well formed in the configurations
\* built to make hosts disagree about what the configuration is.
DecisionsCountOnlyMembers ==
    \A k \in Versions : \A e \in counted[k] : e.quorum \subseteq Members[e.config]


\* Every host counted in a decision at version k was serving exactly instance k
\* when it was counted, which is the property the version gate exists for.
DecisionsCountOnlyCaughtUpHosts ==
    \A k \in Versions : \A e \in counted[k] : e.caughtUp


\* No decision's quorum spans two chains. This is what the chain identity buys
\* at the recorder, and it is checked separately from Agreement because the
\* mixing is reachable one decision before a fork is.
NoCrossClusterDecision ==
    \A k \in Versions : \A e \in counted[k] : e.oneCluster


\* If anything has been decided, some live host still holds a record at the
\* highest decided version. This is the decommission gate as a checked rule
\* rather than as prose: it says recovery remains possible, and says nothing
\* about whether it happens.
LatestDecisionSurvives ==
    \* The empty chain is an IF on the DecommissionSafe precedent: state
    \* predicates evaluate disjunctions lazily today, but this is the same shape
    \* as its action-side twin and one idiom guards every partial expression.
    IF HighestDecided = 0
    THEN TRUE
    ELSE \E h \in Hosts : alive[h] /\ held[h] \in decisions[HighestDecided]


\* The member assignments the configurations name, because a configuration file
\* cannot write a function down. ChangeMembers is an ordinary reconfiguration
\* whose two majorities are already disjoint; DisjointMembers is the headline,
\* where the member sets themselves share nothing; SplitMembers is the pair of
\* genesis lists an operator produces by editing one host's deployment file.
ChangeMembers == [c \in Configs |-> IF c = 1 THEN {1, 2, 3} ELSE {3, 4, 5}]

DisjointMembers == [c \in Configs |-> IF c = 1 THEN {1, 2, 3} ELSE {4, 5}]

SplitMembers == [c \in Configs |-> IF c = 1 THEN {1, 2, 3} ELSE {1, 2, 4}]


\* The chain assignments. OneCluster is one register reconfiguring itself, which
\* is every configuration except the genesis trio. ClusterPerConfig gives each
\* configuration its own chain identity, which is what the digest of a differing
\* member array produces and is the only way a genesis split is expressible.
OneCluster == [c \in Configs |-> 1]

ClusterPerConfig == [c \in Configs |-> c]


\* The genesis assignments. UniformGenesis is the deployment that got its files
\* right. SplitGenesis is one host provisioned with a different member list,
\* which is the base case no derivation can close and no model can check away.
UniformGenesis == [h \in Hosts |-> 1]

SplitGenesis == [h \in Hosts |-> IF h = 4 THEN 2 ELSE 1]


\* The local configurations read only when the derivation is withdrawn.
\* UniformLocal keeps that state space unchanged wherever the rule is in force,
\* and DividedLocal is the disagreement the withdrawal exists to model.
UniformLocal == [h \in Hosts |-> 1]

DividedLocal == [h \in Hosts |-> IF h \in {1, 2} THEN 1 ELSE 2]

=============================================================================
