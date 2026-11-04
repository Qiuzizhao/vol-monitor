import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import ts from 'typescript';
const source=fs.readFileSync(new URL('../src/fitGrid.ts',import.meta.url),'utf8');
const js=ts.transpileModule(source,{compilerOptions:{module:ts.ModuleKind.ESNext,target:ts.ScriptTarget.ES2022}}).outputText;
const {fitGrid}=await import('data:text/javascript;base64,'+Buffer.from(js).toString('base64'));
for(const [width,height,rows,columns] of [[1200,480,7,7],[1800,800,10,12],[700,340,12,16],[320,450,7,7],[280,120,30,30]]){
 test(`${rows}×${columns} fits ${width}×${height} without overflowing either axis`,()=>{
  const fit=fitGrid(width,height,rows,columns);
  assert.ok(Math.abs(fit.cellWidth*columns+fit.gap*(columns-1)-width)<1e-8);
  assert.ok(Math.abs(fit.cellHeight*rows+fit.gap*(rows-1)-height)<1e-8);
  assert.ok(fit.labelFont+fit.valueFont+fit.stateFont+2*fit.padding<=fit.cellHeight+1e-8);
  assert.ok(fit.valueFont*2.4+2*fit.padding<=fit.cellWidth+1e-8);
  assert.ok(Object.values(fit).every(n=>Number.isFinite(n)&&n>=0));
 });
}
test('editor includes the header row and column, leaving no scroll area',()=>{
 const fit=fitGrid(1200,500,31,31,0);
 assert.equal(fit.gap,0);assert.ok(fit.cellHeight*31<=500+1e-8);
 assert.ok(fit.editorFont*1.2+2<=fit.cellHeight);
});
test('large displays preserve readable maximum fonts and smaller screens shrink cells',()=>{
 const large=fitGrid(1800,900,6,6),small=fitGrid(800,350,8,8);
 assert.equal(large.valueFont,34);assert.equal(large.labelFont,15);
 assert.ok(small.valueFont<large.valueFont);assert.ok(small.cellHeight<large.cellHeight);
});
test('initial hidden grid has finite styles until ResizeObserver measures it',()=>{
 assert.ok(Object.values(fitGrid(0,0,7,7)).every(n=>n===0));
});
