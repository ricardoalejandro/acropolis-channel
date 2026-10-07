export function hasControlCharacters(value: string, allowLineBreaks = false) {
  for (const character of value) {
    const code = character.charCodeAt(0);
    if (code === 127 || (code < 32 && !(allowLineBreaks && [9, 10, 13].includes(code))))
      return true;
  }
  return false;
}
