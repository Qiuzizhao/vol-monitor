export interface CellPoint { row:number; col:number }
export interface CellRange { top:number; left:number; bottom:number; right:number }
export function cellRange(a:CellPoint,b:CellPoint):CellRange {
 return {top:Math.min(a.row,b.row),left:Math.min(a.col,b.col),bottom:Math.max(a.row,b.row),right:Math.max(a.col,b.col)};
}
export function fillRange(source:CellRange,target:CellPoint):CellRange {
 const horizontal=Math.max(source.left-target.col,target.col-source.right,0);
 const vertical=Math.max(source.top-target.row,target.row-source.bottom,0);
 return horizontal>=vertical&&horizontal>0
  ? {...source,left:Math.min(source.left,target.col),right:Math.max(source.right,target.col)}
  : {...source,top:Math.min(source.top,target.row),bottom:Math.max(source.bottom,target.row)};
}
function series(values:string[],index:number):string {
 const parsed=values.map(value=>{
  const match=value.match(/^(-?\d+)$|^(.*?)(\d+)$/);
  if(!match)return null;
  const digits=match[1]??match[3],prefix=match[1]?'':match[2];
  return {prefix,value:Number(digits),width:/^0\d/.test(digits)?digits.length:0};
 });
 const first=parsed[0],second=parsed[1];
 const step=first&&second?second.value-first.value:1;
 if(first&&parsed.every((v,i)=>v&&v.prefix===first.prefix&&Number.isSafeInteger(v.value)&&v.value===first.value+i*step)){
  const value=first.value+index*step;
  if(Number.isSafeInteger(value))return first.prefix+(value<0?'-':'')+Math.abs(value).toString().padStart(first.width,'0');
 }
 return values[((index%values.length)+values.length)%values.length];
}
export function fillCells(cells:string[][],source:CellRange,target:CellPoint):string[][] {
 const lastRow=cells.length-1,lastCol=cells[0].length-1;
 const range=fillRange(source,{row:Math.max(0,Math.min(lastRow,target.row)),col:Math.max(0,Math.min(lastCol,target.col))});
 const result=cells.map(row=>[...row]);
 const horizontal=range.left!==source.left||range.right!==source.right;
 for(let r=range.top;r<=range.bottom;r++)for(let c=range.left;c<=range.right;c++){
  if(r>=source.top&&r<=source.bottom&&c>=source.left&&c<=source.right)continue;
  const values=horizontal?cells[r].slice(source.left,source.right+1):cells.slice(source.top,source.bottom+1).map(row=>row[c]);
  result[r][c]=series(values,horizontal?c-source.left:r-source.top);
 }
 return result;
}
