-- Governance schema step 0047 (PostgreSQL): four-eyes approval for grants on secret data (issue #1121). The first of two approvals of a
-- grant on secret data is an approval step; the step keeps every personal identifier form of its approver so a second approval by the
-- same person under another identifier form is refused. Existing steps keep NULL (compared by approver_sid).

ALTER TABLE APPROVAL_STEPS ADD COLUMN IF NOT EXISTS approver_identifiers_json TEXT NULL;
