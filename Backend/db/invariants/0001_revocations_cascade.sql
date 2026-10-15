-- Violations: a live session whose credential is revoked, or whose account is suspended (revocation cascades, Stage 3).
select s.session_id
from auth_sessions s
join dev_credentials c on c.credential_id = s.credential_id
where s.revoked_at is null and c.revoked_at is not null
union all
select s.session_id
from auth_sessions s
join accounts a on a.account_id = s.account_id
where s.revoked_at is null and a.status = 'suspended';
