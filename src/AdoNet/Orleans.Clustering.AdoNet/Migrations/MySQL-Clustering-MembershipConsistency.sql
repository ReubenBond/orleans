-- Run after the existing clustering schema migrations, before deploying the updated membership provider.
-- Stop every silo using these membership queries before migration; restart only with the updated provider.
-- Providers cache OrleansQuery text, and the new cleanup query requires parameters absent in older providers.
DROP PROCEDURE IF EXISTS InsertMembershipKey;
DROP PROCEDURE IF EXISTS UpdateMembershipKey;
DROP PROCEDURE IF EXISTS CleanupDefunctSiloEntriesKey;
DELIMITER $$

CREATE PROCEDURE InsertMembershipKey(
    IN _DeploymentId NVARCHAR(150),
    IN _Address VARCHAR(45),
    IN _Port INT,
    IN _Generation INT,
    IN _Version INT,
    IN _SiloName NVARCHAR(150),
    IN _HostName NVARCHAR(150),
    IN _Status INT,
    IN _ProxyPort INT,
    IN _StartTime DATETIME,
    IN _IAmAliveTime DATETIME)
BEGIN
    DECLARE _ROWCOUNT INT;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;
    START TRANSACTION;
    UPDATE OrleansMembershipVersionTable
    SET Version = Version + 1
    WHERE DeploymentId = _DeploymentId AND Version = _Version AND Version < 2147483647;
    SET _ROWCOUNT = ROW_COUNT();

    INSERT INTO OrleansMembershipTable
        (DeploymentId, Address, Port, Generation, SiloName, HostName, Status, ProxyPort, StartTime, IAmAliveTime)
    SELECT _DeploymentId, _Address, _Port, _Generation, _SiloName, _HostName, _Status, _ProxyPort, _StartTime, _IAmAliveTime
    FROM DUAL WHERE _ROWCOUNT > 0 AND NOT EXISTS
    (
        SELECT 1 FROM OrleansMembershipTable
        WHERE DeploymentId = _DeploymentId AND Address = _Address
            AND Port = _Port AND Generation = _Generation
    );
    SET _ROWCOUNT = ROW_COUNT();

    IF _ROWCOUNT = 0 THEN
        ROLLBACK;
    ELSE
        COMMIT;
    END IF;
    SELECT _ROWCOUNT;
END$$

CREATE PROCEDURE UpdateMembershipKey(
    IN _DeploymentId NVARCHAR(150),
    IN _Address VARCHAR(45),
    IN _Port INT,
    IN _Generation INT,
    IN _Version INT,
    IN _Status INT,
    IN _SuspectTimes VARCHAR(8000),
    IN _IAmAliveTime DATETIME)
BEGIN
    DECLARE _ROWCOUNT INT;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;
    START TRANSACTION;
    UPDATE OrleansMembershipVersionTable
    SET Version = Version + 1
    WHERE DeploymentId = _DeploymentId AND Version = _Version AND Version < 2147483647;
    SET _ROWCOUNT = ROW_COUNT();

    UPDATE OrleansMembershipTable
    SET Status = _Status, SuspectTimes = _SuspectTimes,
        IAmAliveTime = GREATEST(IAmAliveTime, _IAmAliveTime)
    WHERE DeploymentId = _DeploymentId AND Address = _Address
        AND Port = _Port AND Generation = _Generation AND _ROWCOUNT > 0;
    IF _ROWCOUNT > 0 THEN
        SELECT COUNT(*) INTO _ROWCOUNT FROM OrleansMembershipTable
        WHERE DeploymentId = _DeploymentId AND Address = _Address
            AND Port = _Port AND Generation = _Generation;
    END IF;

    IF _ROWCOUNT = 0 THEN
        ROLLBACK;
    ELSE
        COMMIT;
    END IF;
    SELECT _ROWCOUNT;
END$$

CREATE PROCEDURE CleanupDefunctSiloEntriesKey(
    IN _DeploymentId NVARCHAR(150),
    IN _Address VARCHAR(45),
    IN _Port INT,
    IN _Generation INT,
    IN _Version INT,
    IN _IAmAliveTime DATETIME,
    IN _StartTime DATETIME,
    IN _SuspectTimes VARCHAR(8000))
BEGIN
    DECLARE _ROWCOUNT INT;
    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        ROLLBACK;
        RESIGNAL;
    END;
    START TRANSACTION;
    UPDATE OrleansMembershipVersionTable
    SET Version = Version + 1
    WHERE DeploymentId = _DeploymentId AND Version = _Version AND Version < 2147483647;
    SET _ROWCOUNT = ROW_COUNT();

    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = _DeploymentId AND Status = 6
        AND Address = _Address AND Port = _Port AND Generation = _Generation
        AND IAmAliveTime = _IAmAliveTime AND StartTime = _StartTime
        AND COALESCE(SuspectTimes, '') = COALESCE(_SuspectTimes, '')
        AND _ROWCOUNT > 0;
    SET _ROWCOUNT = ROW_COUNT();
    IF _ROWCOUNT = 0 THEN
        ROLLBACK;
    ELSE
        COMMIT;
    END IF;
    SELECT _ROWCOUNT;
END$$

DELIMITER ;

UPDATE OrleansQuery SET QueryText = '
UPDATE OrleansMembershipTable
SET IAmAliveTime = GREATEST(IAmAliveTime, @IAmAliveTime)
WHERE DeploymentId = @DeploymentId AND Address = @Address
    AND Port = @Port AND Generation = @Generation;'
WHERE QueryKey = 'UpdateIAmAlivetimeKey';

UPDATE OrleansQuery SET QueryText = '
CALL UpdateMembershipKey(@DeploymentId, @Address, @Port, @Generation,
    @Version, @Status, @SuspectTimes, @IAmAliveTime);'
WHERE QueryKey = 'UpdateMembershipKey';

UPDATE OrleansQuery SET QueryText = '
CALL CleanupDefunctSiloEntriesKey(@DeploymentId, @Address, @Port, @Generation,
    @Version, @IAmAliveTime, @StartTime, @SuspectTimes);'
WHERE QueryKey = 'CleanupDefunctSiloEntriesKey';
