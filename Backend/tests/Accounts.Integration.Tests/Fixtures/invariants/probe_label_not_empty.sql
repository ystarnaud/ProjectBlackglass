-- Fixture invariant for the upgrade-harness test: every probe row has a label.
select id from probe where label = '';
