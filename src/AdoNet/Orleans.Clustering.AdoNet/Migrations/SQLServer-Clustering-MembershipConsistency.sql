-- Run after the existing clustering schema migrations, before deploying the updated membership provider.
-- Stop every silo using these membership queries before migration; restart only with the updated provider.
-- Providers cache OrleansQuery text, and the new cleanup query requires parameters absent in older providers.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

UPDATE OrleansQuery SET QueryText = '
SET NOCOUNT ON;
UPDATE OrleansMembershipTable
SET IAmAliveTime = CASE WHEN IAmAliveTime > @IAmAliveTime THEN IAmAliveTime ELSE @IAmAliveTime END
WHERE DeploymentId = @DeploymentId AND Address = @Address
    AND Port = @Port AND Generation = @Generation;'
WHERE QueryKey = 'UpdateIAmAlivetimeKey';

UPDATE OrleansQuery SET QueryText = '
SET XACT_ABORT, NOCOUNT ON;
DECLARE @ROWCOUNT INT;
BEGIN TRANSACTION;
UPDATE OrleansMembershipVersionTable
SET Timestamp = GETUTCDATE(), Version = Version + 1
WHERE DeploymentId = @DeploymentId AND Version = @Version AND Version < 2147483647;
SET @ROWCOUNT = @@ROWCOUNT;

INSERT INTO OrleansMembershipTable
    (DeploymentId, Address, Port, Generation, SiloName, HostName, Status, ProxyPort, StartTime, IAmAliveTime)
SELECT @DeploymentId, @Address, @Port, @Generation, @SiloName, @HostName, @Status, @ProxyPort, @StartTime, @IAmAliveTime
WHERE @ROWCOUNT > 0 AND NOT EXISTS
(
    SELECT 1 FROM OrleansMembershipTable WITH(HOLDLOCK, XLOCK, ROWLOCK)
    WHERE DeploymentId = @DeploymentId AND Address = @Address
        AND Port = @Port AND Generation = @Generation
);
SET @ROWCOUNT = @@ROWCOUNT;
IF @ROWCOUNT = 0
    ROLLBACK TRANSACTION;
ELSE
    COMMIT TRANSACTION;
SELECT @ROWCOUNT;'
WHERE QueryKey = 'InsertMembershipKey';

UPDATE OrleansQuery SET QueryText = '
SET XACT_ABORT, NOCOUNT ON;
DECLARE @ROWCOUNT INT;
BEGIN TRANSACTION;
UPDATE OrleansMembershipVersionTable
SET Timestamp = GETUTCDATE(), Version = Version + 1
WHERE DeploymentId = @DeploymentId AND Version = @Version AND Version < 2147483647;

UPDATE OrleansMembershipTable
SET Status = @Status, SuspectTimes = @SuspectTimes,
    IAmAliveTime = CASE WHEN IAmAliveTime > @IAmAliveTime THEN IAmAliveTime ELSE @IAmAliveTime END
WHERE DeploymentId = @DeploymentId AND Address = @Address
    AND Port = @Port AND Generation = @Generation AND @@ROWCOUNT > 0;
SET @ROWCOUNT = @@ROWCOUNT;
IF @ROWCOUNT = 0
    ROLLBACK TRANSACTION;
ELSE
    COMMIT TRANSACTION;
SELECT @ROWCOUNT;'
WHERE QueryKey = 'UpdateMembershipKey';

UPDATE OrleansQuery SET QueryText = '
SELECT v.DeploymentId, m.Address, m.Port, m.Generation, m.SiloName, m.HostName,
    m.Status, m.ProxyPort, m.SuspectTimes, m.StartTime, m.IAmAliveTime, v.Version
FROM OrleansMembershipVersionTable v WITH(HOLDLOCK)
LEFT OUTER JOIN OrleansMembershipTable m WITH(HOLDLOCK)
    ON v.DeploymentId = m.DeploymentId
    AND m.Address = @Address AND m.Port = @Port AND m.Generation = @Generation
WHERE v.DeploymentId = @DeploymentId;'
WHERE QueryKey = 'MembershipReadRowKey';

UPDATE OrleansQuery SET QueryText = '
SELECT v.DeploymentId, m.Address, m.Port, m.Generation, m.SiloName, m.HostName,
    m.Status, m.ProxyPort, m.SuspectTimes, m.StartTime, m.IAmAliveTime, v.Version
FROM OrleansMembershipVersionTable v WITH(HOLDLOCK)
LEFT OUTER JOIN OrleansMembershipTable m WITH(HOLDLOCK) ON v.DeploymentId = m.DeploymentId
WHERE v.DeploymentId = @DeploymentId;'
WHERE QueryKey = 'MembershipReadAllKey';

UPDATE OrleansQuery SET QueryText = '
SET XACT_ABORT, NOCOUNT ON;
DECLARE @ROWCOUNT INT;
BEGIN TRANSACTION;
UPDATE OrleansMembershipVersionTable
SET Timestamp = GETUTCDATE(), Version = Version + 1
WHERE DeploymentId = @DeploymentId AND Version = @Version AND Version < 2147483647;

DELETE FROM OrleansMembershipTable
WHERE DeploymentId = @DeploymentId AND Status = 6
    AND IAmAliveTime = @IAmAliveTime AND StartTime = @StartTime
    AND Address = @Address AND Port = @Port AND Generation = @Generation
    AND COALESCE(SuspectTimes, '''') = COALESCE(@SuspectTimes, '''')
    AND @@ROWCOUNT > 0;
SET @ROWCOUNT = @@ROWCOUNT;
IF @ROWCOUNT = 0
    ROLLBACK TRANSACTION;
ELSE
    COMMIT TRANSACTION;
SELECT @ROWCOUNT;'
WHERE QueryKey = 'CleanupDefunctSiloEntriesKey';

COMMIT TRANSACTION;
