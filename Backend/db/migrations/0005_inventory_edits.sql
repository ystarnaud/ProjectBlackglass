-- 0005 inventory edits (Stage 5; spec §5.3, rulings S5-D3, S5-D4). Edits move instances between the stash, bags and slots,
-- and change stack quantities; they never create or destroy an instance or touch a definition, account, origin or identity.
grant update (operative_id, equipped_slot) on item_instances to accounts_app;
grant update (quantity), delete on item_stacks to accounts_app;
