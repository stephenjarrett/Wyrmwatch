"""Read-only diagnosis of the already-approved v0.2.3 draft; no mutation calls."""
import json
import os
import subprocess

REPOSITORY = "stephenjarrett/Wyrmwatch"
TAG = "v0.2.3"
HEAD = "eda347fa38bceec59fd5a89774ae7c5ef877f4f6"


def read(arguments):
    env = os.environ.copy()
    env.pop("GH_DEBUG", None)
    result = subprocess.run(["gh", *arguments], capture_output=True, env=env)
    if result.returncode:
        raise RuntimeError(result.stderr.decode(errors="replace").strip())
    return json.loads(result.stdout)


def describe(item):
    return {key: item.get(key) for key in ("id", "tag_name", "draft", "target_commitish", "published_at")}


def main():
    if os.environ.get("GITHUB_REPOSITORY") != REPOSITORY:
        raise RuntimeError("This one-off diagnostic is restricted to its approved repository")
    root = "repos/" + REPOSITORY
    releases = read(["api", "--method", "GET", root + "/releases?per_page=100"])
    print("REST release inventory:", json.dumps([describe(item) for item in releases]))
    owner, name = REPOSITORY.split("/")
    query = """query($owner:String!, $name:String!) {
      repository(owner:$owner, name:$name) {
        releases(first:100, orderBy:{field:CREATED_AT,direction:DESC}) {
          nodes { databaseId tagName isDraft publishedAt }
          pageInfo { hasNextPage }
        }
      }
    }"""
    graph = read(["api", "graphql", "-f", "query=" + query, "-f", "owner=" + owner, "-f", "name=" + name])
    if graph.get("errors"):
        raise RuntimeError("GraphQL release inventory query failed")
    inventory = graph["data"]["repository"]["releases"]
    print("GraphQL release inventory:", json.dumps(inventory))
    # The first read-only run found our exact-source draft under an untagged name.
    # Inspect that already-observed candidate as data; never adopt or mutate it.
    ids = {item["id"] for item in releases if item.get("tag_name") == TAG
           or (item.get("draft") and item.get("target_commitish") == HEAD)}
    ids.update(item["databaseId"] for item in inventory["nodes"] if item["tagName"] == TAG)
    for release_id in sorted(ids):
        item = read(["api", "--method", "GET", root + "/releases/" + str(release_id)])
        body = item.get("body") or ""
        import re
        markers = re.findall(r"<!-- wyrmwatch-release-receipt-v1 (.*?) -->", body)
        metadata = json.loads(markers[0]) if len(markers) == 1 else None
        print("Matching draft by ID:", json.dumps(dict(describe(item),
              name=item.get("name"), updated_at=item.get("updated_at"), receipt=metadata,
              source_receipt_matches=HEAD in body and "wyrmwatch-release-receipt-v1" in body,
              assets=[{"name": asset["name"], "digest": asset.get("digest")} for asset in item.get("assets", [])])))
    print("Read-only diagnosis complete; matching IDs:", sorted(ids))


if __name__ == "__main__":
    main()
