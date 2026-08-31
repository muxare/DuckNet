export type WikiToken =
  | { type: "text"; value: string }
  | { type: "link"; id: string; label: string };

const LINK = /\[\[([^\]|]+)(?:\|([^\]]+))?\]\]/g;

export function parseWiki(body: string): WikiToken[] {
  const tokens: WikiToken[] = [];
  let last = 0;
  const re = new RegExp(LINK.source, "g");
  let match: RegExpExecArray | null;
  while ((match = re.exec(body)) !== null) {
    if (match.index > last) {
      tokens.push({ type: "text", value: body.slice(last, match.index) });
    }
    const id = match[1].trim();
    const label = (match[2] ?? match[1]).trim();
    tokens.push({ type: "link", id, label });
    last = match.index + match[0].length;
  }
  if (last < body.length) {
    tokens.push({ type: "text", value: body.slice(last) });
  }
  return tokens;
}

export function wikiLinkIds(body: string): string[] {
  return parseWiki(body)
    .filter((token): token is Extract<WikiToken, { type: "link" }> => token.type === "link")
    .map((token) => token.id);
}

export function unknownWikiIds(body: string, known: Set<string>): string[] {
  return wikiLinkIds(body).filter((id) => !known.has(id));
}

export function paragraphs(body: string): string[] {
  return body
    .split(/\n\n+/)
    .map((part) => part.trim())
    .filter((part) => part.length > 0);
}
