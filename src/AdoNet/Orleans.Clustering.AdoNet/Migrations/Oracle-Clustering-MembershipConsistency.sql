-- Run after the existing clustering schema migrations, before deploying the updated membership provider.
-- Stop every silo using these membership queries before migration; restart only with the updated provider.
-- Providers cache OrleansQuery text, and the new cleanup query requires parameters absent in older providers.
CREATE OR REPLACE FUNCTION InsertMembership(
    PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_IAMALIVETIME IN TIMESTAMP,
    PARAM_SILONAME IN NVARCHAR2, PARAM_HOSTNAME IN NVARCHAR2, PARAM_ADDRESS IN VARCHAR2,
    PARAM_PORT IN NUMBER, PARAM_GENERATION IN NUMBER, PARAM_STARTTIME IN TIMESTAMP,
    PARAM_STATUS IN NUMBER, PARAM_PROXYPORT IN NUMBER, PARAM_VERSION IN NUMBER)
RETURN NUMBER IS
    rowcount NUMBER;
    PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
    UPDATE OrleansMembershipVersionTable
    SET Timestamp = sys_extract_utc(systimestamp), Version = Version + 1
    WHERE DeploymentId = PARAM_DEPLOYMENTID AND Version = PARAM_VERSION AND Version < 2147483647;
    rowcount := SQL%ROWCOUNT;

    INSERT INTO OrleansMembershipTable
        (DeploymentId, Address, Port, Generation, SiloName, HostName, Status, ProxyPort, StartTime, IAmAliveTime)
    SELECT PARAM_DEPLOYMENTID, PARAM_ADDRESS, PARAM_PORT, PARAM_GENERATION, PARAM_SILONAME,
        PARAM_HOSTNAME, PARAM_STATUS, PARAM_PROXYPORT, PARAM_STARTTIME, PARAM_IAMALIVETIME
    FROM DUAL WHERE rowcount > 0 AND NOT EXISTS
    (
        SELECT 1 FROM OrleansMembershipTable
        WHERE DeploymentId = PARAM_DEPLOYMENTID AND Address = PARAM_ADDRESS
            AND Port = PARAM_PORT AND Generation = PARAM_GENERATION
    );
    rowcount := SQL%ROWCOUNT;
    IF rowcount = 0 THEN
        ROLLBACK;
    ELSE
        COMMIT;
    END IF;
    RETURN(rowcount);
END;
/

CREATE OR REPLACE FUNCTION UpdateMembership(
    PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_ADDRESS IN VARCHAR2,
    PARAM_PORT IN NUMBER, PARAM_GENERATION IN NUMBER, PARAM_IAMALIVETIME IN TIMESTAMP,
    PARAM_STATUS IN NUMBER, PARAM_SUSPECTTIMES IN VARCHAR2, PARAM_VERSION IN NUMBER)
RETURN NUMBER IS
    rowcount NUMBER;
    PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
    UPDATE OrleansMembershipVersionTable
    SET Timestamp = sys_extract_utc(systimestamp), Version = Version + 1
    WHERE DeploymentId = PARAM_DEPLOYMENTID AND Version = PARAM_VERSION AND Version < 2147483647;
    rowcount := SQL%ROWCOUNT;

    UPDATE OrleansMembershipTable
    SET Status = PARAM_STATUS, SuspectTimes = PARAM_SUSPECTTIMES,
        IAmAliveTime = GREATEST(IAmAliveTime, PARAM_IAMALIVETIME)
    WHERE DeploymentId = PARAM_DEPLOYMENTID AND Address = PARAM_ADDRESS
        AND Port = PARAM_PORT AND Generation = PARAM_GENERATION AND rowcount > 0;
    rowcount := SQL%ROWCOUNT;
    IF rowcount = 0 THEN
        ROLLBACK;
    ELSE
        COMMIT;
    END IF;
    RETURN(rowcount);
END;
/

CREATE OR REPLACE FUNCTION UpdateIAmAlivetime(
    PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_ADDRESS IN VARCHAR2,
    PARAM_PORT IN NUMBER, PARAM_GENERATION IN NUMBER, PARAM_IAMALIVE IN TIMESTAMP)
RETURN NUMBER IS
    PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
    UPDATE OrleansMembershipTable
    SET IAmAliveTime = GREATEST(IAmAliveTime, PARAM_IAMALIVE)
    WHERE DeploymentId = PARAM_DEPLOYMENTID AND Address = PARAM_ADDRESS
        AND Port = PARAM_PORT AND Generation = PARAM_GENERATION;
    COMMIT;
    RETURN(0);
END;
/

CREATE OR REPLACE FUNCTION CleanupDefunctSiloEntry(
    PARAM_DEPLOYMENTID IN NVARCHAR2, PARAM_ADDRESS IN VARCHAR2,
    PARAM_PORT IN NUMBER, PARAM_GENERATION IN NUMBER, PARAM_VERSION IN NUMBER,
    PARAM_IAMALIVETIME IN TIMESTAMP, PARAM_STARTTIME IN TIMESTAMP, PARAM_SUSPECTTIMES IN VARCHAR2)
RETURN NUMBER IS
    rowcount NUMBER;
    PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
    UPDATE OrleansMembershipVersionTable
    SET Timestamp = sys_extract_utc(systimestamp), Version = Version + 1
    WHERE DeploymentId = PARAM_DEPLOYMENTID AND Version = PARAM_VERSION AND Version < 2147483647;
    rowcount := SQL%ROWCOUNT;

    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = PARAM_DEPLOYMENTID AND Status = 6
        AND Address = PARAM_ADDRESS AND Port = PARAM_PORT AND Generation = PARAM_GENERATION
        AND IAmAliveTime = PARAM_IAMALIVETIME AND StartTime = PARAM_STARTTIME
        AND (SuspectTimes = PARAM_SUSPECTTIMES OR (SuspectTimes IS NULL AND PARAM_SUSPECTTIMES IS NULL))
        AND rowcount > 0;
    rowcount := SQL%ROWCOUNT;
    IF rowcount = 0 THEN
        ROLLBACK;
    ELSE
        COMMIT;
    END IF;
    RETURN(rowcount);
END;
/

UPDATE OrleansQuery SET QueryText = '
SELECT CleanupDefunctSiloEntry(:DeploymentId, :Address, :Port, :Generation,
    :Version, :IAmAliveTime, :StartTime, :SuspectTimes) FROM DUAL'
WHERE QueryKey = 'CleanupDefunctSiloEntriesKey';
/
COMMIT;
