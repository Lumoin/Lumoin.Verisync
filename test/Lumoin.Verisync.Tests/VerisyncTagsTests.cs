using Lumoin.Verisync.Core;

namespace Lumoin.Verisync.Tests;

[TestClass]
internal sealed class VerisyncTagsTests
{
    [TestMethod]
    public void ReplicaIdTagCarriesReplicaIdKind()
    {
        Assert.AreEqual(VerisyncKind.ReplicaId, VerisyncTags.ReplicaId.Get<VerisyncKind>());
    }


    [TestMethod]
    public void OperationIdTagCarriesOperationIdKind()
    {
        Assert.AreEqual(VerisyncKind.OperationId, VerisyncTags.OperationId.Get<VerisyncKind>());
    }


    [TestMethod]
    public void BallotEncodingTagCarriesBallotEncodingKind()
    {
        Assert.AreEqual(VerisyncKind.BallotEncoding, VerisyncTags.BallotEncoding.Get<VerisyncKind>());
    }


    [TestMethod]
    public void SerializedDeltaTagCarriesSerializedDeltaKind()
    {
        Assert.AreEqual(VerisyncKind.SerializedDelta, VerisyncTags.SerializedDelta.Get<VerisyncKind>());
    }


    [TestMethod]
    public void AuthorizationWitnessTagCarriesAuthorizationWitnessKind()
    {
        Assert.AreEqual(VerisyncKind.AuthorizationWitness, VerisyncTags.AuthorizationWitness.Get<VerisyncKind>());
    }


    [TestMethod]
    public void RegisterValueBytesTagCarriesRegisterValueBytesKind()
    {
        Assert.AreEqual(VerisyncKind.RegisterValueBytes, VerisyncTags.RegisterValueBytes.Get<VerisyncKind>());
    }


    [TestMethod]
    public void GossipDigestTagCarriesGossipDigestKind()
    {
        Assert.AreEqual(VerisyncKind.GossipDigest, VerisyncTags.GossipDigest.Get<VerisyncKind>());
    }


    [TestMethod]
    public void TagsAreSharedSingletons()
    {
        Assert.AreSame(VerisyncTags.ReplicaId, VerisyncTags.ReplicaId);
        Assert.AreSame(VerisyncTags.GossipDigest, VerisyncTags.GossipDigest);
    }


    [TestMethod]
    public void EveryKindHasAPreBuiltTag()
    {
        VerisyncKind[] expectedKinds =
        [
            VerisyncKind.ReplicaId,
            VerisyncKind.OperationId,
            VerisyncKind.BallotEncoding,
            VerisyncKind.SerializedDelta,
            VerisyncKind.AuthorizationWitness,
            VerisyncKind.RegisterValueBytes,
            VerisyncKind.GossipDigest
        ];

        Assert.HasCount(expectedKinds.Length, Enum.GetValues<VerisyncKind>());

        Tag[] tags =
        [
            VerisyncTags.ReplicaId,
            VerisyncTags.OperationId,
            VerisyncTags.BallotEncoding,
            VerisyncTags.SerializedDelta,
            VerisyncTags.AuthorizationWitness,
            VerisyncTags.RegisterValueBytes,
            VerisyncTags.GossipDigest
        ];

        for(int i = 0; i < expectedKinds.Length; i++)
        {
            Assert.AreEqual(expectedKinds[i], tags[i].Get<VerisyncKind>());
        }
    }
}
