# Project Board API Reference

Technical reference for programmatic access to the OpenAgent GitHub Project board.

## Key IDs

**Project:** `PVT_kwHOAAlkGM4BRQEy` (project #4)

**Status field:** `PVTSSF_lAHOAAlkGM4BRQEyzg_IiCs`

| Status | Option ID |
|--------|-----------|
| Ideas | `cdd1924e` |
| Roadmap | `73df9a8e` |
| Backlog | `52baef61` |
| In Progress | `cf923259` |
| Done | `c131faac` |

**Description field:** `PVTF_lAHOAAlkGM4BRQEyzg_IiDM`

**Views:**
| View | ID |
|------|-----|
| Overview | `PVTV_lAHOAAlkGM4BRQEyzgJmBT8` |
| Ideas | `PVTV_lAHOAAlkGM4BRQEyzgJmBUA` |
| Roadmap | `PVTV_lAHOAAlkGM4BRQEyzgJmBUE` |
| Backlog | `PVTV_lAHOAAlkGM4BRQEyzgJmBUI` |
| In Progress | `PVTV_lAHOAAlkGM4BRQEyzgJmBUM` |
| Done | `PVTV_lAHOAAlkGM4BRQEyzgJmBUQ` |

## GraphQL Examples

### Reorder items

Move an item to a specific position in its column:

```graphql
mutation {
  updateProjectV2ItemPosition(input: {
    projectId: "PVT_kwHOAAlkGM4BRQEy"
    itemId: "ITEM_ID"
    afterId: "AFTER_ITEM_ID"
  }) { clientMutationId }
}
```

Omit `afterId` to move an item to the top.

### Change status

```graphql
mutation {
  updateProjectV2ItemFieldValue(input: {
    projectId: "PVT_kwHOAAlkGM4BRQEy"
    itemId: "ITEM_ID"
    fieldId: "PVTSSF_lAHOAAlkGM4BRQEyzg_IiCs"
    value: { singleSelectOptionId: "52baef61" }
  }) { clientMutationId }
}
```

### Set description

```graphql
mutation {
  updateProjectV2ItemFieldValue(input: {
    projectId: "PVT_kwHOAAlkGM4BRQEy"
    itemId: "ITEM_ID"
    fieldId: "PVTF_lAHOAAlkGM4BRQEyzg_IiDM"
    value: { text: "Short description (max 128 chars)" }
  }) { clientMutationId }
}
```

## CLI Examples

### Create a new backlog item

```bash
# Create issue
url=$(gh issue create \
  --repo mbundgaard/OpenAgent \
  --title "My feature" \
  --body "Details here

## Source

- [Original discussion](https://example.com/link)" \
  --label "agent,size:M")

# Add to project
item_id=$(gh project item-add 4 --owner mbundgaard --url "$url" --format json | jq -r '.id')

# Set status to Backlog
gh api graphql -f query='
  mutation {
    updateProjectV2ItemFieldValue(input: {
      projectId: "PVT_kwHOAAlkGM4BRQEy"
      itemId: "'"$item_id"'"
      fieldId: "PVTSSF_lAHOAAlkGM4BRQEyzg_IiCs"
      value: { singleSelectOptionId: "52baef61" }
    }) { clientMutationId }
  }'
```

### Note for Git Bash (Windows)

Prefix `gh project` and `gh api` commands with `MSYS_NO_PATHCONV=1` to prevent path mangling.
