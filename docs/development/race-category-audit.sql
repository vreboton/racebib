-- Run with a read-only connection to an authorized RaceBib database.
-- Include inactive categories because historical registrations can reference them.
SELECT r.EventId, r.Id AS RaceId, r.Name AS RaceName,
       COUNT(c.Id) AS CategoryCount,
       SUM(CASE WHEN c.Status = 'Active' THEN 1 ELSE 0 END) AS ActiveCategoryCount
FROM Races AS r
LEFT JOIN RaceCategories AS c ON c.RaceId = r.Id
GROUP BY r.EventId, r.Id, r.Name
HAVING COUNT(c.Id) <> 1 OR ActiveCategoryCount <> 1
ORDER BY r.EventId, r.Name, r.Id;
